using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Telegram.Interop;

namespace AltyaziDB.Player.Telegram;

public sealed class TdJsonTelegramService : ITelegramService
{
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _downloads = new();
    private readonly ConcurrentDictionary<int, TelegramFileSnapshot> _fileStates = new();
    private readonly ConcurrentDictionary<int, AsyncPulse> _filePulses = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _rangeRequestGates = new();
    // User-requested stops must not be undone by a subsequent libmpv seek.
    private readonly ConcurrentDictionary<int, byte> _stoppedTransfers = new();
    private readonly ConcurrentDictionary<int, RemoteOpenRequest> _streamRequests = new();
    private readonly ConcurrentDictionary<string, RemoteOpenRequest> _archiveStreamRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TelegramArchiveCacheSession> _archiveCacheSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, string> _chatTitles = new();
    private IReadOnlyList<TelegramChatFolderItem> _chatFolders = Array.Empty<TelegramChatFolderItem>();
    private readonly CancellationTokenSource _lifetime = new();
    private TdJsonNativeLibrary? _native;
    private Task? _receiveTask;
    private TelegramClientConfiguration? _configuration;
    private int _clientId;
    private long _requestSequence;
    private int _parametersSent;
    private TelegramRangeStreamServer? _streamServer;
    private bool _disposed;

    public TdJsonTelegramService(IAppLogger logger)
    {
        _logger = logger;
        IsNativeAvailable = TdJsonNativeLibrary.TryLoad(out _native, out var diagnostic);
        NativeDiagnostic = diagnostic;
        Authorization = TelegramAuthorizationSnapshot.NotStarted;
        if (IsNativeAvailable) _logger.Info(diagnostic);
        else _logger.Warning(diagnostic);
    }

    public event EventHandler<TelegramAuthorizationSnapshot>? AuthorizationChanged;
    public event EventHandler<TelegramDownloadProgress>? DownloadProgressChanged;
    public event EventHandler<IReadOnlyList<TelegramChatFolderItem>>? ChatFoldersChanged;

    public bool IsNativeAvailable { get; }
    public string NativeDiagnostic { get; }
    public TelegramAuthorizationSnapshot Authorization { get; private set; }
    public IReadOnlyList<TelegramChatFolderItem> ChatFolders => _chatFolders;

    public async Task<long?> GetCurrentUserIdAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        // TDLib getMe identifies the actually authorized user, not a stale
        // phone number, cached chat title, or machine-local session directory.
        var me = await RequestAsync(
            new Dictionary<string, object?> { ["@type"] = "getMe" },
            cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var id = ReadInt64(me, "id");
        return id > 0 ? id : null;
    }

    public async Task StartAsync(TelegramClientConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_native is null) throw new InvalidOperationException(NativeDiagnostic);
        if (configuration.ApiId <= 0 || string.IsNullOrWhiteSpace(configuration.ApiHash))
            throw new InvalidOperationException("Telegram API ID ve API Hash gereklidir.");

        _configuration = configuration;
        Directory.CreateDirectory(configuration.DatabaseDirectory);
        Directory.CreateDirectory(configuration.FilesDirectory);
        if (_clientId == 0) _clientId = _native.CreateClientId();
        _receiveTask ??= Task.Run(() => ReceiveLoopAsync(_lifetime.Token), CancellationToken.None);
        ExecuteBestEffort(new Dictionary<string, object?>
        {
            ["@type"] = "setLogVerbosityLevel",
            ["new_verbosity_level"] = 1
        });
        SetAuthorization(new(TelegramAuthorizationStage.Starting, "TDLib yetkilendirme durumu alınıyor…"));
        var state = await RequestAsync(new Dictionary<string, object?> { ["@type"] = "getAuthorizationState" }, cancellationToken)
            .ConfigureAwait(false);
        await ProcessAuthorizationStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private readonly SemaphoreSlim _cacheOptimizationGate = new(1, 1);

    public async Task<TelegramCacheUsage> GetCacheUsageAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var result = await RequestAsync(new Dictionary<string, object?>
        {
            ["@type"] = "getStorageStatisticsFast"
        }, cancellationToken, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var directory = _configuration?.FilesDirectory ?? string.Empty;
        return new TelegramCacheUsage(
            Math.Max(0L, ReadInt64(result, "files_size")),
            Math.Max(0, ReadInt32(result, "file_count")),
            Math.Max(0L, ReadInt64(result, "database_size")),
            GetArchiveTemporarySize(directory),
            directory);
    }

    public async Task<TelegramCacheUsage> OptimizeCacheAsync(
        long targetBytes, bool manual, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (targetBytes < 0 || targetBytes > 2L * 1024 * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(targetBytes));
        await _cacheOptimizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // TDLib is the only authority allowed to remove downloaded Telegram
            // files. Its databases, encryption keys, and active archive sessions
            // are never deleted by a filesystem sweep.
            _ = await RequestAsync(new Dictionary<string, object?>
            {
                ["@type"] = "optimizeStorage",
                ["size"] = targetBytes,
                ["ttl"] = -1,
                ["count"] = -1,
                // Preserve recently used chunks during automatic maintenance.
                ["immunity_delay"] = manual ? 0 : 3600,
                ["file_types"] = Array.Empty<object>(),
                ["chat_ids"] = Array.Empty<long>(),
                ["exclude_chat_ids"] = Array.Empty<long>(),
                ["return_deleted_file_statistics"] = false,
                ["chat_limit"] = 0
            }, cancellationToken, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            CleanupOrphanedArchiveCache();
            _logger.Info($"Telegram önbellek bakımı: manuel={manual}, hedefBayt={targetBytes}");
            return await GetCacheUsageAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cacheOptimizationGate.Release();
        }
    }

    private static long GetArchiveTemporarySize(string filesDirectory)
    {
        if (string.IsNullOrWhiteSpace(filesDirectory)) return 0;
        var root = Path.Combine(filesDirectory, "archive-stream-cache");
        if (!Directory.Exists(root)) return 0;
        long sum = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { sum = checked(sum + new FileInfo(file).Length); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (OverflowException) { return long.MaxValue; }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return sum;
    }

    private void CleanupOrphanedArchiveCache()
    {
        var filesDirectory = _configuration?.FilesDirectory;
        if (string.IsNullOrWhiteSpace(filesDirectory)) return;
        var root = Path.Combine(filesDirectory, "archive-stream-cache");
        if (!Directory.Exists(root)) return;
        // Crash leftovers only. Never touch directories owned by a currently
        // running stream, nor recent directories other instances may use.
        var active = _archiveCacheSessions.Values
            .Select(session => Path.GetFullPath(session.SessionDirectory))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var full = Path.GetFullPath(folder);
                    if (active.Contains(full) ||
                        Directory.GetLastWriteTimeUtc(full) > DateTime.UtcNow.AddDays(-1) ||
                        !IsOrphanedArchiveFolderSafeToRemove(full))
                        continue;
                    Directory.Delete(full, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsOrphanedArchiveFolderSafeToRemove(string folder)
    {
        // Protect folders still being written by another ADB Player instance,
        // including ones older than a day whose directory timestamp is stale.
        var threshold = DateTime.UtcNow.AddDays(-1);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(file) > threshold) return false;
                using var exclusive = new FileStream(file, FileMode.Open,
                    FileAccess.Read, FileShare.None);
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public Task SubmitPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default) =>
        RequestVoidAsync(new Dictionary<string, object?>
        {
            ["@type"] = "setAuthenticationPhoneNumber",
            ["phone_number"] = phoneNumber.Trim()
        }, cancellationToken);

    public Task SubmitEmailAddressAsync(string emailAddress, CancellationToken cancellationToken = default) =>
        RequestVoidAsync(new Dictionary<string, object?>
        {
            ["@type"] = "setAuthenticationEmailAddress",
            ["email_address"] = emailAddress.Trim()
        }, cancellationToken);

    public Task SubmitEmailCodeAsync(string code, CancellationToken cancellationToken = default) =>
        RequestVoidAsync(new Dictionary<string, object?>
        {
            ["@type"] = "checkAuthenticationEmailCode",
            ["code"] = new Dictionary<string, object?>
            {
                ["@type"] = "emailAddressAuthenticationCode",
                ["code"] = code.Trim()
            }
        }, cancellationToken);

    public Task SubmitCodeAsync(string code, CancellationToken cancellationToken = default) =>
        RequestVoidAsync(new Dictionary<string, object?>
        {
            ["@type"] = "checkAuthenticationCode",
            ["code"] = code.Trim()
        }, cancellationToken);

    public Task SubmitPasswordAsync(string password, CancellationToken cancellationToken = default) =>
        RequestVoidAsync(new Dictionary<string, object?>
        {
            ["@type"] = "checkAuthenticationPassword",
            ["password"] = password
        }, cancellationToken);

    public Task RegisterUserAsync(string firstName, string lastName, CancellationToken cancellationToken = default) =>
        RequestVoidAsync(new Dictionary<string, object?>
        {
            ["@type"] = "registerUser",
            ["first_name"] = firstName.Trim(),
            ["last_name"] = lastName.Trim()
        }, cancellationToken);

    public Task SignOutAsync(CancellationToken cancellationToken = default) =>
        RequestVoidAsync(new Dictionary<string, object?> { ["@type"] = "logOut" }, cancellationToken);

    public async Task<IReadOnlyList<TelegramChatItem>> GetChatsAsync(
        int limit = 200,
        int? chatFolderId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        limit = Math.Clamp(limit, 1, 1000);
        var chatList = chatFolderId switch
        {
            null => new Dictionary<string, object?> { ["@type"] = "chatListMain" },
            -1 => new Dictionary<string, object?> { ["@type"] = "chatListArchive" },
            _ => new Dictionary<string, object?>
            {
                ["@type"] = "chatListFolder",
                ["chat_folder_id"] = chatFolderId.Value
            }
        };
        try
        {
            await RequestVoidAsync(new Dictionary<string, object?>
            {
                ["@type"] = "loadChats",
                ["chat_list"] = chatList,
                ["limit"] = Math.Min(limit, 100)
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (TelegramRequestException exception) when (exception.Code == 404)
        {
            // Listenin sonuna ulaşılması TDLib tarafından 404 ile bildirilebilir.
        }

        var response = await RequestAsync(new Dictionary<string, object?>
        {
            ["@type"] = "getChats",
            ["chat_list"] = chatList,
            ["limit"] = limit
        }, cancellationToken).ConfigureAwait(false);

        var ids = response.TryGetProperty("chat_ids", out var rawIds) && rawIds.ValueKind == JsonValueKind.Array
            ? rawIds.EnumerateArray().Select(ParseInt64).Where(value => value != 0).ToArray()
            : [];

        var chats = new List<TelegramChatItem>();
        foreach (var batch in ids.Chunk(24))
        {
            var results = await Task.WhenAll(batch.Select(id => GetChatAsync(id, cancellationToken))).ConfigureAwait(false);
            chats.AddRange(results.Where(item => item is not null).Select(item => item!));
        }
        return chats.OrderByDescending(item => item.Order).ThenBy(item => item.Title).ToArray();
    }

    public async Task<IReadOnlyList<TelegramForumTopicItem>> GetForumTopicsAsync(
        long chatId,
        string? query = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        limit = Math.Clamp(limit, 1, 100);
        try
        {
            var response = await RequestAsync(new Dictionary<string, object?>
            {
                ["@type"] = "getForumTopics",
                ["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture),
                ["query"] = query?.Trim() ?? string.Empty,
                ["offset_date"] = 0,
                ["offset_message_id"] = "0",
                ["offset_forum_topic_id"] = 0,
                ["limit"] = limit
            }, cancellationToken).ConfigureAwait(false);

            if (!response.TryGetProperty("topics", out var rawTopics) || rawTopics.ValueKind != JsonValueKind.Array)
                return [];

            var topics = new List<TelegramForumTopicItem>();
            foreach (var rawTopic in rawTopics.EnumerateArray())
            {
                if (!rawTopic.TryGetProperty("info", out var info)) continue;
                var id = ReadInt32(info, "forum_topic_id");
                if (id <= 0) continue;
                topics.Add(new TelegramForumTopicItem(
                    chatId,
                    id,
                    ReadString(info, "name", $"Konu {id}"),
                    ReadInt32(rawTopic, "unread_count"),
                    ReadInt64(rawTopic, "order"),
                    ReadBool(rawTopic, "is_pinned"),
                    ReadBool(info, "is_closed"),
                    ReadBool(info, "is_general")));
            }

            return topics
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .OrderByDescending(item => item.IsPinned)
                .ThenByDescending(item => item.Order)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (TelegramRequestException exception) when (exception.Code is 400 or 404)
        {
            // Normal chats/channels don't expose forum topics.
            return [];
        }
    }

    public async Task<IReadOnlyList<TelegramMediaItem>> GetChatMediaAsync(
        long chatId,
        string? query = null,
        int limit = 300,
        CancellationToken cancellationToken = default,
        int? forumTopicId = null)
    {
        EnsureReady();
        // TDLib searchChatMessages returns at most 100 *and may return fewer*.
        // A single request cannot enumerate a Telegram group/topic history.
        limit = Math.Clamp(limit, 1, 2500);
        var messages = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (forumTopicId is > 0)
        {
            // Dedicated forum topic history avoids search-index gaps where
            // Telegram .7z.001/.002 messages used to disappear from the UI.
            try
            {
                var history = await TelegramForumTopicHistoryPager.ReadAsync(
                    (cursor, take, token) => RequestAsync(new Dictionary<string, object?>
                    {
                        ["@type"] = "getForumTopicHistory",
                        ["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture),
                        ["forum_topic_id"] = forumTopicId.Value,
                        ["from_message_id"] = cursor.ToString(CultureInfo.InvariantCulture),
                        ["offset"] = 0,
                        ["limit"] = take
                    }, token),
                    limit, cancellationToken).ConfigureAwait(false);
                // Signed chat IDs and deduplication are common to both
                // topic history and search-index responses.
                TelegramForumMessageMerge.Add(messages, history);
                _logger.Info($"Telegram konu geçmişi: topic={forumTopicId.Value}, mesaj={messages.Count}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Warning($"Telegram konu geçmişi okunamadı; arama yedeğine geçiliyor: {exception.Message}");
            }
        }
        // Fetch all forum documents/videos without media-type filtering
        // so .001/.002 and .z01/.zip cannot disappear independently.
        string?[] filters = forumTopicId is > 0
            ? [null]
            : ["searchMessagesFilterVideo", "searchMessagesFilterDocument",
               "searchMessagesFilterAnimation", "searchMessagesFilterAudio"];
        // Always merge topic history with search results. TDLib may return
        // an incomplete history page despite remaining messages, and search
        // may know other documents. Use chat ID + message ID for deduplication.
        var historyMessageCount = messages.Count;
        foreach (var filter in filters)
        {
            long cursor = 0;
            var seen = new HashSet<long>();
            var fetched = 0;
            try
            {
                // Each media type is paginated independently, otherwise many
                // videos can hide all documents when the final page is capped.
                while (fetched < limit)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var take = Math.Min(100, limit - fetched);
                    var response = await RequestAsync(new Dictionary<string, object?>
                    {
                        ["@type"] = "searchChatMessages",
                        ["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture),
                        ["topic_id"] = forumTopicId is > 0
                            ? new Dictionary<string, object?>
                            {
                                ["@type"] = "messageTopicForum",
                                ["forum_topic_id"] = forumTopicId.Value
                            }
                            : null,
                        ["query"] = forumTopicId is > 0 ? string.Empty : query?.Trim() ?? string.Empty,
                        ["sender_id"] = null,
                        ["from_message_id"] = cursor.ToString(CultureInfo.InvariantCulture),
                        ["offset"] = 0,
                        ["limit"] = take,
                        ["filter"] = filter is null ? null :
                            new Dictionary<string, object?> { ["@type"] = filter }
                    }, cancellationToken).ConfigureAwait(false);

                    if (!response.TryGetProperty("messages", out var batch) ||
                        batch.ValueKind != JsonValueKind.Array ||
                        batch.GetArrayLength() == 0) break;

                    long oldest = long.MaxValue;
                    var advanced = 0;
                    foreach (var message in batch.EnumerateArray())
                    {
                        var id = ReadInt64(message, "id");
                        if (id <= 0) continue;
                        oldest = Math.Min(oldest, id);
                        if (!seen.Add(id)) continue;
                        var resultChatId = ReadInt64(message, "chat_id");
                        if (resultChatId == 0) continue;
                        messages[$"{resultChatId}:{id}"] = message.Clone();
                        advanced++;
                    }

                    if (oldest == long.MaxValue || advanced == 0 ||
                        (cursor > 0 && oldest >= cursor)) break;
                    fetched += advanced;
                    cursor = oldest;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A filter failure must not hide results from the other filters.
                _logger.Warning($"Telegram medya sayfalaması kullanılamadı ({filter}): {exception.Message}");
            }
        }

        _logger.Info($"Telegram medya birlestirme: topic={forumTopicId}, gecmis={historyMessageCount}, toplam={messages.Count}");

        // Some Telegram servers do not support the media-type filter together
        // with forum topic_id. Use a paged unfiltered topic search rather than
        // reporting an existing topic as empty.
        if (messages.Count == 0 && forumTopicId is > 0)
        {
            long cursor = 0;
            for (var page = 0; page < Math.Min(25, (limit + 99) / 100); page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await RequestAsync(new Dictionary<string, object?>
                {
                    ["@type"] = "searchChatMessages",
                    ["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture),
                    ["topic_id"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "messageTopicForum",
                        ["forum_topic_id"] = forumTopicId.Value
                    },
                    ["query"] = forumTopicId is > 0 ? string.Empty : query?.Trim() ?? string.Empty,
                    ["sender_id"] = null,
                    ["from_message_id"] = cursor.ToString(CultureInfo.InvariantCulture),
                    ["offset"] = 0,
                    ["limit"] = Math.Min(100, limit),
                    ["filter"] = null
                }, cancellationToken).ConfigureAwait(false);
                if (!response.TryGetProperty("messages", out var batch) ||
                    batch.ValueKind != JsonValueKind.Array ||
                    batch.GetArrayLength() == 0) break;
                AddMessages(messages, response);
                var ids = batch.EnumerateArray().Select(m => ReadInt64(m, "id"))
                    .Where(id => id > 0).ToArray();
                if (ids.Length == 0) break;
                var oldest = ids.Min();
                if (cursor > 0 && oldest >= cursor) break;
                cursor = oldest;
                if (messages.Count >= limit) break;
            }
        }

        // Historical fallback for chats where server-side filters are
        // unavailable. Do not replace a partial filtered result with chat text.
        if (messages.Count == 0 && forumTopicId is null)
        {
            long cursor = 0;
            for (var page = 0; page < Math.Min(25, (limit + 99) / 100); page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var history = await RequestAsync(new Dictionary<string, object?>
                {
                    ["@type"] = "getChatHistory",
                    ["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture),
                    ["from_message_id"] = cursor.ToString(CultureInfo.InvariantCulture),
                    ["offset"] = 0,
                    ["limit"] = 100,
                    ["only_local"] = false
                }, cancellationToken).ConfigureAwait(false);
                if (!history.TryGetProperty("messages", out var batch) ||
                    batch.ValueKind != JsonValueKind.Array ||
                    batch.GetArrayLength() == 0) break;

                AddMessages(messages, history);
                var ids = batch.EnumerateArray().Select(m => ReadInt64(m, "id"))
                    .Where(id => id > 0).ToArray();
                if (ids.Length == 0) break;
                var oldest = ids.Min();
                if (cursor > 0 && oldest >= cursor) break;
                cursor = oldest;
                if (messages.Count >= limit) break;
            }
        }

        var title = await GetChatTitleAsync(chatId, cancellationToken).ConfigureAwait(false);
        var parsed = messages.Values
            .Select(message => ParseMedia(message, title))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Date)
            .ThenByDescending(item => item.MessageId)
            .Take(limit)
            .ToArray();
        // Multipart grouping MUST happen before applying the search phrase:
        // captions can differ between .001/.002 and .z01/.zip messages.
        // Filtering physical messages first loses a required part.
        var grouped = TelegramArchiveSetDetector.Group(parsed);
        _logger.Info($"Telegram dosya listesi: topic={forumTopicId}, mesaj={messages.Count}, ek={parsed.Length}, kart={grouped.Count}");
        if (string.IsNullOrWhiteSpace(query)) return grouped;
        var wanted = query.Trim();
        return grouped.Where(item =>
            item.FileName.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
            item.Caption.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
            (item.ArchiveParts?.Any(part =>
                part.FileName.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                part.Caption.Contains(wanted, StringComparison.OrdinalIgnoreCase)) == true))
            .ToArray();
    }

    public async Task<IReadOnlyList<TelegramMediaItem>> SearchMediaAsync(string query, int limit = 100, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return [];
        limit = Math.Clamp(limit, 1, 100);
        var messages = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var filter in new[] { "searchMessagesFilterVideo", "searchMessagesFilterDocument", "searchMessagesFilterAnimation", "searchMessagesFilterAudio" })
        {
            try
            {
                var response = await RequestAsync(new Dictionary<string, object?>
                {
                    ["@type"] = "searchMessages",
                    ["chat_list"] = null,
                    ["query"] = query.Trim(),
                    ["offset"] = string.Empty,
                    ["limit"] = limit,
                    ["filter"] = new Dictionary<string, object?> { ["@type"] = filter },
                    ["chat_type_filter"] = null,
                    ["min_date"] = 0,
                    ["max_date"] = 0
                }, cancellationToken).ConfigureAwait(false);
                AddMessages(messages, response);
            }
            catch (Exception exception)
            {
                _logger.Warning($"Telegram genel arama filtresi kullanılamadı ({filter}): {exception.Message}");
            }
        }

        var results = new List<TelegramMediaItem>();
        foreach (var message in messages.Values.OrderByDescending(item => ReadInt32(item, "date")))
        {
            var chatId = ReadInt64(message, "chat_id");
            var title = await GetChatTitleAsync(chatId, cancellationToken).ConfigureAwait(false);
            var media = ParseMedia(message, title);
            if (media is not null) results.Add(media);
            if (results.Count >= limit) break;
        }
        return TelegramArchiveSetDetector.Group(results);
    }

    private static int[] MediaTransferFileIds(TelegramMediaItem media) =>
        (media.ArchiveParts is { Count: > 0 }
            ? media.ArchiveParts.Select(part => part.FileId)
            : [media.FileId])
        .Where(id => id > 0)
        .Distinct()
        .ToArray();

    private void ThrowIfTransferStopped(int fileId)
    {
        if (_stoppedTransfers.ContainsKey(fileId))
            throw new OperationCanceledException("Telegram indirmesi kullanici tarafindan durduruldu.");
    }

    public async Task StopMediaTransferAsync(
        TelegramMediaItem media, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(media);
        var ids = MediaTransferFileIds(media);
        foreach (var id in ids)
        {
            _stoppedTransfers.TryAdd(id, 0);
            if (_downloads.TryRemove(id, out var waiter))
                waiter.TrySetCanceled();
            _filePulses.GetOrAdd(id, _ => new AsyncPulse()).Pulse();
        }

        // A progressive compressed archive has an independent extraction
        // worker. Dispose its cache session when the user stops the transfer,
        // otherwise that worker could continue reading TDLib file ranges.
        if (media.IsArchiveSet)
        {
            var cachePrefix = $"cache:{media.ChatId}:{media.ArchiveFormat}:{media.ArchiveBaseName}:";
            foreach (var key in _archiveCacheSessions.Keys
                .Where(key => key.StartsWith(cachePrefix, StringComparison.Ordinal)).ToArray())
            {
                if (_archiveCacheSessions.TryRemove(key, out var session))
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    _archiveStreamRequests.TryRemove(key, out _);
                }
            }
        }

        var failures = new List<Exception>();
        foreach (var id in ids)
        {
            try
            {
                await RequestVoidAsync(new Dictionary<string, object?>
                {
                    ["@type"] = "cancelDownloadFile",
                    ["file_id"] = id,
                    ["only_if_pending"] = false
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failures.Add(error);
                _logger.Warning($"Telegram aktarimi durdurulamadi (file={id}): {error.Message}");
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("Telegram aktarimini durdurma komutu tamamlanamadi.", failures);
    }

    public void ResumeMediaTransfer(TelegramMediaItem media)
    {
        ArgumentNullException.ThrowIfNull(media);
        foreach (var id in MediaTransferFileIds(media))
        {
            _stoppedTransfers.TryRemove(id, out _);
            _filePulses.GetOrAdd(id, _ => new AsyncPulse()).Pulse();
        }
    }

    public async Task<string> DownloadMediaAsync(TelegramMediaItem media, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (media.IsDownloaded && !string.IsNullOrWhiteSpace(media.LocalPath) && File.Exists(media.LocalPath))
            return media.LocalPath;
        if (media.FileId <= 0) throw new InvalidOperationException("Telegram dosya kimliği bulunamadı.");

        var waiter = _downloads.GetOrAdd(media.FileId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            var response = await RequestAsync(new Dictionary<string, object?>
            {
                ["@type"] = "downloadFile",
                ["file_id"] = media.FileId,
                ["priority"] = 16,
                ["offset"] = 0,
                ["limit"] = 0,
                ["synchronous"] = false
            }, cancellationToken, TimeSpan.FromSeconds(45)).ConfigureAwait(false);

            var immediate = ParseFile(response);
            if (immediate.IsCompleted && !string.IsNullOrWhiteSpace(immediate.Path) && File.Exists(immediate.Path))
                return immediate.Path;

            return await waiter.Task.WaitAsync(TimeSpan.FromHours(6), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _downloads.TryRemove(media.FileId, out _);
        }
    }

    public async Task<RemoteOpenRequest> CreateStreamRequestAsync(
        TelegramMediaItem media,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        cancellationToken.ThrowIfCancellationRequested();
        if (media.FileId <= 0) throw new InvalidOperationException("Telegram dosya kimliği bulunamadı.");
        if (media.Size <= 0) throw new InvalidOperationException("Telegram dosya boyutu alınamadı; medya akışı başlatılamıyor.");

        // URL'yi libmpv'ye vermeden once yalnizca ilk medya blogunun okunabilir
        // oldugunu dogrula. Bu tam indirme degildir; oynaticinin baslik verisini
        // ilk HTTP isteginde hemen almasini saglayan kisa bir baslangic tamponudur.
        using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            startup.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                var probeSize = (int)Math.Min(256 * 1024L, media.Size);
                var probe = await ReadStreamRangeAsync(media.FileId, media.Size, 0, probeSize, startup.Token)
                    .ConfigureAwait(false);
                if (probe.Length == 0)
                    throw new InvalidOperationException("Telegram ilk medya bloğunu hazırlayamadı.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Telegram medya akışı 45 saniye içinde başlatılamadı.");
            }
        }

        var request = _streamRequests.GetOrAdd(media.FileId, _ =>
        {
            _streamServer ??= new TelegramRangeStreamServer((message, exception) =>
                _logger.Warning($"{message} {exception?.Message}".Trim()));
            var source = _streamServer.Register(
                media.FileName,
                string.IsNullOrWhiteSpace(media.MimeType) ? "application/octet-stream" : media.MimeType,
                media.Size,
                (offset, count, token) => ReadStreamRangeAsync(media.FileId, media.Size, offset, count, token));
            return new RemoteOpenRequest(
                source,
                media.FileName,
                Provider: "telegram-range",
                PersistentSource: $"telegram:{media.ChatId}:{media.MessageId}");
        });
        return request;
    }

    public async Task<TelegramArchiveInspection> InspectArchiveAsync(
        TelegramMediaItem archive,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(archive);
        if (!archive.IsArchiveSet || archive.ArchiveParts is not { Count: > 0 })
            throw new InvalidOperationException("Seçilen öğe çok parçalı bir Telegram arşivi değil.");
        if (!archive.ArchiveSetComplete || archive.ArchiveSetAmbiguous)
            return new TelegramArchiveInspection(
                archive.ArchiveFormat,
                [],
                false,
                archive.ArchiveSetAmbiguous
                    ? "Aynı sıra numarasıyla birden fazla parça bulundu. Parçaları düzenleyin."
                    : "Arşiv parçalarından biri eksik. Parçaları düzenleyin.");

        var map = CreateArchiveMap(archive);
        if (archive.ArchiveFormat is TelegramArchiveFormat.RawSplit or TelegramArchiveFormat.GenericSplit)
        {
            var name = archive.ArchiveBaseName ?? archive.FileName;
            if (archive.ArchiveFormat == TelegramArchiveFormat.GenericSplit)
            {
                // NAME.001/.002 does not reveal the original format. Inspect
                // its actual bytes, not captions, before choosing a reader.
                var signature = await map.ReadExactAsync(
                    0, (int)Math.Min(8L, map.Length), cancellationToken).ConfigureAwait(false);
                if (signature.Length >= 4 && signature[0] == 'P' && signature[1] == 'K' &&
                    ((signature[2] == 3 && signature[3] == 4) ||
                     (signature[2] == 5 && signature[3] == 6)))
                {
                    _logger.Info($"Telegram generic split: concatenated ZIP Store/Deflate · {name}");
                    return await SplitZipArchiveReader.InspectConcatenatedAsync(map, cancellationToken)
                        .ConfigureAwait(false);
                }
                if (signature.Length >= 6 &&
                    signature[0] == 0x37 && signature[1] == 0x7A &&
                    signature[2] == 0xBC && signature[3] == 0xAF &&
                    signature[4] == 0x27 && signature[5] == 0x1C)
                {
                    _logger.Info($"Telegram generic split: 7z archive · {name}");
                    return await TelegramArchiveStreamingEngine.InspectAsync(
                        map, TelegramArchiveFormat.GenericSplit, cancellationToken).ConfigureAwait(false);
                }
                if (signature.Length >= 4 && signature[0] == 'R' && signature[1] == 'a' &&
                    signature[2] == 'r' && signature[3] == '!')
                {
                    _logger.Info($"Telegram generic split: RAR archive · {name}");
                    return await TelegramArchiveStreamingEngine.InspectAsync(
                        map, TelegramArchiveFormat.GenericSplit, cancellationToken).ConfigureAwait(false);
                }
                // Raw media remains a direct, gapless Range stream. libmpv
                // probes formats from bytes even when the uploader removed
                // the .mkv/.mp4 suffix from the split file's base name.
                if (signature.Length >= 4 &&
                    signature[0] == 0x1A && signature[1] == 0x45 &&
                    signature[2] == 0xDF && signature[3] == 0xA3)
                    name += ".mkv";
                else if (signature.Length >= 8 &&
                         signature[4] == 'f' && signature[5] == 't' &&
                         signature[6] == 'y' && signature[7] == 'p')
                    name += ".mp4";
                _logger.Info($"Telegram generic split: raw media · {name}");
            }
            return new TelegramArchiveInspection(
                archive.ArchiveFormat,
                [new TelegramArchiveEntry(name, map.Length, map.Length, 0, 0, false, true)],
                true,
                "Dosya parçaları sırayla, tamamının indirilmesi beklenmeden açılabilir.");
        }
        var inspection = archive.ArchiveFormat == TelegramArchiveFormat.SplitZip
            ? await SplitZipArchiveReader.InspectAsync(map, cancellationToken).ConfigureAwait(false)
            : await TelegramArchiveStreamingEngine.InspectAsync(map, archive.ArchiveFormat, cancellationToken)
                .ConfigureAwait(false);
        _logger.Info($"Telegram arşiv okuma yolu: format={archive.ArchiveFormat}, " +
            $"direkt={inspection.Entries.Count(entry => entry.UsesDirectRange)}, " +
            $"tamponlu={inspection.Entries.Count(entry => entry.IsPlayable && !entry.UsesDirectRange)}");
        return inspection;
    }

    public async Task<RemoteOpenRequest> CreateArchiveStreamRequestAsync(
        TelegramMediaItem archive,
        TelegramArchiveEntry entry,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entry);
        if (archive.ArchiveFormat == TelegramArchiveFormat.None || archive.ArchiveParts is not { Count: > 0 })
            throw new InvalidOperationException("Seçilen öğe geçerli bir çok parçalı arşiv değil.");
        if (!archive.ArchiveSetComplete || archive.ArchiveSetAmbiguous)
            throw new InvalidOperationException("Arşiv parçaları eksik veya belirsiz. Önce parçaları düzenleyin.");
        if (!entry.IsPlayable) throw new InvalidOperationException("Seçilen arşiv girdisi oynatılabilir bir medya değil.");
        if (entry.IsEncrypted) throw new InvalidOperationException("Şifreli arşivler henüz anlık oynatılamıyor.");
        if (entry.UncompressedSize <= 0) throw new InvalidOperationException("Arşivdeki videonun boyutu alınamadı.");

        var map = CreateArchiveMap(archive);
        if (!entry.UsesDirectRange)
            return await CreateBufferedArchiveStreamRequestAsync(archive, entry, map, cancellationToken)
                .ConfigureAwait(false);

        if (entry.DataOffset > map.Length - entry.UncompressedSize)
            throw new InvalidDataException("Arşiv içi video byte aralığı geçersiz.");

        using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            startup.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                var probeSize = (int)Math.Min(256 * 1024L, entry.UncompressedSize);
                _ = await map.ReadExactAsync(entry.DataOffset, probeSize, startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Arşiv içindeki video akışı 45 saniye içinde başlatılamadı.");
            }
        }

        var key = $"{archive.ChatId}:{archive.ArchiveBaseName}:{entry.Name}:{entry.DataOffset}:{entry.UncompressedSize}";
        return _archiveStreamRequests.GetOrAdd(key, _ =>
        {
            _streamServer ??= new TelegramRangeStreamServer((message, exception) =>
                _logger.Warning($"{message} {exception?.Message}".Trim()));
            var source = _streamServer.Register(
                Path.GetFileName(entry.Name),
                GetArchiveMediaType(entry.Name),
                entry.UncompressedSize,
                (offset, count, token) => map.ReadExactAsync(checked(entry.DataOffset + offset), count, token));
            return new RemoteOpenRequest(
                source,
                Path.GetFileName(entry.Name),
                Provider: "telegram-split-zip",
                PersistentSource: $"telegram-archive:{archive.ChatId}:{archive.ArchiveBaseName}:{entry.Name}");
        });
    }

    private async Task<RemoteOpenRequest> CreateBufferedArchiveStreamRequestAsync(
        TelegramMediaItem archive,
        TelegramArchiveEntry entry,
        SplitArchiveVolumeMap map,
        CancellationToken cancellationToken)
    {
        var key = $"cache:{archive.ChatId}:{archive.ArchiveFormat}:{archive.ArchiveBaseName}:{entry.Name}:{entry.UncompressedSize}";
        if (_archiveStreamRequests.TryGetValue(key, out var existing)) return existing;

        var cacheRoot = Path.Combine(
            _configuration?.FilesDirectory ?? Path.GetTempPath(),
            "archive-stream-cache");
        var candidate = new TelegramArchiveCacheSession(
            map,
            archive.ArchiveFormat,
            entry,
            cacheRoot,
            (message, exception) => _logger.Warning($"{message} {exception?.Message}".Trim()));
        var session = _archiveCacheSessions.GetOrAdd(key, candidate);
        if (!ReferenceEquals(session, candidate)) await candidate.DisposeAsync().ConfigureAwait(false);

        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_archiveCacheSessions.TryRemove(key, out var failed))
                await failed.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return _archiveStreamRequests.GetOrAdd(key, _ =>
        {
            _streamServer ??= new TelegramRangeStreamServer((message, exception) =>
                _logger.Warning($"{message} {exception?.Message}".Trim()));
            var source = _streamServer.Register(
                Path.GetFileName(entry.Name),
                GetArchiveMediaType(entry.Name),
                session.Length,
                session.ReadAsync);
            return new RemoteOpenRequest(
                source,
                Path.GetFileName(entry.Name),
                Provider: "telegram-archive-buffer",
                PersistentSource: $"telegram-archive:{archive.ChatId}:{archive.ArchiveBaseName}:{entry.Name}");
        });
    }


    /// <summary>
    /// Fetches only the selected subtitle entry's compressed bytes from TDLib.
    /// No DownloadMediaAsync and no entire Telegram ZIP are required. The tiny
    /// extracted subtitle is kept locally because libmpv needs a real file for
    /// subtitle sync and ASS parsing.
    /// </summary>
    public async Task<string> ExtractArchiveSubtitleAsync(
        TelegramMediaItem archive,
        TelegramArchiveEntry entry,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entry);
        if (archive.ArchiveFormat is not (TelegramArchiveFormat.SplitZip or TelegramArchiveFormat.GenericSplit) ||
            archive.ArchiveParts is not { Count: > 0 } ||
            !archive.ArchiveSetComplete || archive.ArchiveSetAmbiguous)
            throw new InvalidDataException("Altyazı için geçerli bir ZIP kaynağı seçin.");
        var extension = Path.GetExtension(entry.Name);
        if (!new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup" }.Contains(extension))
            throw new InvalidDataException("Arşiv girdisi desteklenen bir altyazı değil.");
        const long maximumSubtitleBytes = 128L * 1024 * 1024;
        if (entry.IsEncrypted || entry.UncompressedSize <= 0 ||
            entry.UncompressedSize > maximumSubtitleBytes ||
            entry.CompressedSize <= 0 ||
            entry.CompressionMethod is not (0 or 8))
            throw new InvalidDataException("Şifreli, boş veya desteklenmeyen ZIP altyazısı.");
        if (entry.CompressionMethod == 0 && entry.CompressedSize != entry.UncompressedSize)
            throw new InvalidDataException("ZIP altyazı boyutu tutarsız.");

        var map = CreateArchiveMap(archive);
        if (archive.ArchiveFormat == TelegramArchiveFormat.GenericSplit)
        {
            var magic = await map.ReadExactAsync(0, (int)Math.Min(4L, map.Length), cancellationToken)
                .ConfigureAwait(false);
            if (magic.Length < 4 || magic[0] != 'P' || magic[1] != 'K')
                throw new InvalidDataException("Altyazı çıkarma için bir ZIP dosyası gereklidir.");
        }
        if (entry.DataOffset < 0 || entry.DataOffset > map.Length - entry.CompressedSize)
            throw new InvalidDataException("ZIP içindeki altyazı byte aralığı geçersiz.");

        var fileName = Path.GetFileName(entry.Name.Replace('\\', '/'));
        var folder = Path.Combine(
            _configuration?.FilesDirectory ?? Path.GetTempPath(),
            "archive-subtitle-cache", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(folder, fileName);
        Directory.CreateDirectory(folder);
        try
        {
            using var compressed = new TelegramArchiveReadStream(
                entry.CompressedSize,
                (offset, count, token) => map.ReadExactAsync(
                    checked(entry.DataOffset + offset), count, token),
                cancellationToken);
            using Stream decoded = entry.CompressionMethod == 8
                ? new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true)
                : compressed;

            await using var output = new FileStream(
                outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            long written = 0;
            int read;
            while ((read = await decoded.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                written += read;
                if (written > entry.UncompressedSize || written > maximumSubtitleBytes)
                    throw new InvalidDataException("ZIP altyazısı güvenli çözme sınırını aşıyor.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (written != entry.UncompressedSize)
                throw new InvalidDataException("ZIP altyazısı eksik çözüldü.");
            return outputPath;
        }
        catch
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
            throw;
        }
    }

    private SplitArchiveVolumeMap CreateArchiveMap(TelegramMediaItem archive) => new(
        archive.ArchiveParts ?? throw new InvalidOperationException("Arşiv parçaları bulunamadı."),
        (part, offset, count, token) => ReadStreamRangeAsync(part.FileId, part.Size, offset, count, token));

    private static string GetArchiveMediaType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mkv" or ".mka" => "video/x-matroska",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".ts" or ".m2ts" => "video/mp2t",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".aac" => "audio/aac",
        ".ac3" => "audio/ac3",
        ".eac3" => "audio/eac3",
        ".dts" or ".dtshd" => "audio/vnd.dts",
        ".thd" or ".truehd" => "audio/true-hd",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".opus" => "audio/opus",
        ".ogg" => "audio/ogg",
        _ => "application/octet-stream"
    };

    private async Task<byte[]> ReadStreamRangeAsync(
        int fileId,
        long fileSize,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        ThrowIfTransferStopped(fileId);
        if (fileSize <= 0 || offset < 0 || offset >= fileSize || count < 0)
            throw new EndOfStreamException("Telegram dosyasından istenen byte aralığı geçersiz.");
        count = (int)Math.Min(count, fileSize - offset);
        if (count == 0) return [];
        var pulse = _filePulses.GetOrAdd(fileId, _ => new AsyncPulse());
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        var nextRangeRequest = DateTimeOffset.MinValue;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfTransferStopped(fileId);
            var waitForUpdate = pulse.Next;
            var now = DateTimeOffset.UtcNow;
            if (_fileStates.TryGetValue(fileId, out var current))
            {
                var direct = await TryReadDirectFilePartAsync(current, fileSize, offset, count, cancellationToken)
                    .ConfigureAwait(false);
                if (direct is { Length: > 0 }) return direct;
            }

            if (now >= nextRangeRequest)
            {
                await RequestDownloadRangeAsync(fileId, fileSize, offset, count, cancellationToken).ConfigureAwait(false);
                nextRangeRequest = now.AddSeconds(5);
            }

            // TDLib'nin kendi onbellek sorgusu, local.path bos veya Windows'un
            // dogrudan okuyamayacagi bir yol olsa bile hazir parcayi bildirir.
            try
            {
                var cached = await TryReadTdLibCachePartAsync(fileId, offset, count, cancellationToken)
                    .ConfigureAwait(false);
                if (cached is { Length: > 0 }) return cached;
            }
            catch (TelegramRequestException exception)
            {
                // Eszamanli bir seek istegi indirme ofsetini degistirmis olabilir.
                // Bir sonraki dongu bu ofseti yeniden one alir.
                _logger.Warning($"Telegram önbellek parçası henüz hazır değil: {exception.Message}");
            }

            var delay = Task.Delay(250, cancellationToken);
            await Task.WhenAny(waitForUpdate, delay).ConfigureAwait(false);
        }

        if (_fileStates.TryGetValue(fileId, out var last))
        {
            _logger.Warning(
                $"Telegram akış zaman aşımı: file={fileId}, offset={offset}, " +
                $"downloadOffset={last.DownloadOffset}, prefix={last.DownloadedPrefix}, " +
                $"downloaded={last.Downloaded}, completed={last.IsCompleted}, pathAvailable={File.Exists(last.Path)}");
        }
        else
        {
            _logger.Warning($"Telegram akış zaman aşımı: file={fileId}, offset={offset}, dosya durumu alınamadı.");
        }
        throw new TimeoutException("Telegram medya parçası zamanında alınamadı.");
    }

    private static async Task<byte[]?> TryReadDirectFilePartAsync(
        TelegramFileSnapshot current,
        long fileSize,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(current.Path) || !File.Exists(current.Path)) return null;

        var availableEnd = current.IsCompleted
            ? fileSize
            : current.DownloadOffset + current.DownloadedPrefix;
        if ((!current.IsCompleted && offset < current.DownloadOffset) || offset >= availableEnd) return null;

        var readable = (int)Math.Min(count, availableEnd - offset);
        if (readable <= 0) return null;
        var buffer = new byte[readable];
        await using var file = new FileStream(
            current.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        file.Seek(offset, SeekOrigin.Begin);
        var read = await file.ReadAsync(buffer.AsMemory(0, readable), cancellationToken).ConfigureAwait(false);
        if (read == buffer.Length) return buffer;
        return read > 0 ? buffer[..read] : null;
    }

    private async Task<byte[]?> TryReadTdLibCachePartAsync(
        int fileId,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        var prefixResponse = await RequestAsync(new Dictionary<string, object?>
        {
            ["@type"] = "getFileDownloadedPrefixSize",
            ["file_id"] = fileId,
            ["offset"] = offset
        }, cancellationToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var available = ReadInt64(prefixResponse, "size");
        if (available <= 0) return null;

        var readable = (int)Math.Min(count, Math.Min(available, int.MaxValue));
        if (readable <= 0) return null;
        var partResponse = await RequestAsync(new Dictionary<string, object?>
        {
            ["@type"] = "readFilePart",
            ["file_id"] = fileId,
            ["offset"] = offset,
            ["count"] = readable
        }, cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var encoded = ReadString(partResponse, "data");
        if (string.IsNullOrWhiteSpace(encoded)) return null;

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            return bytes.Length <= readable ? bytes : bytes[..readable];
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("TDLib medya parçası geçerli Base64 verisi içermiyor.", exception);
        }
    }

    private async Task RequestDownloadRangeAsync(
        int fileId,
        long fileSize,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        var gate = _rangeRequestGates.GetOrAdd(fileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfTransferStopped(fileId);
            // Mesaj nesnesindeki local durumu eski bir indirmenin prefix bilgisini
            // tasiyabilir. Dosya temizlenmis olsa bile bu eski deger erken donuse
            // neden oluyordu. Bekleyen her eksik parca TDLib'ye yeniden bildirilir.
            const long readAheadBytes = 2 * 1024 * 1024;
            var limit = Math.Min(fileSize - offset, Math.Max(readAheadBytes, count));
            var response = await RequestAsync(new Dictionary<string, object?>
            {
                ["@type"] = "downloadFile",
                ["file_id"] = fileId,
                ["priority"] = 32,
                ["offset"] = offset,
                ["limit"] = limit,
                ["synchronous"] = false
            }, cancellationToken, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            UpdateFileState(ParseFile(response));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TelegramChatItem?> GetChatAsync(long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await RequestAsync(new Dictionary<string, object?>
            {
                ["@type"] = "getChat",
                ["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken).ConfigureAwait(false);
            var title = ReadString(response, "title", "Adsız sohbet");
            _chatTitles[chatId] = title;
            var type = response.TryGetProperty("type", out var typeElement) ? ReadString(typeElement, "@type", "chat") : "chat";
            var order = 0L;
            if (response.TryGetProperty("positions", out var positions) && positions.ValueKind == JsonValueKind.Array)
            {
                foreach (var position in positions.EnumerateArray())
                {
                    if (position.TryGetProperty("list", out var list) && ReadString(list, "@type") == "chatListMain")
                    {
                        order = ReadInt64(position, "order");
                        break;
                    }
                }
            }
            return new TelegramChatItem(chatId, title, type,
                ReadInt32(response, "unread_count"), order)
            {
                // Telegram channels and groups share chatTypeSupergroup.
                // The is_channel flag is the authoritative TDLib distinction.
                IsChannel = type == "chatTypeSupergroup" &&
                            typeElement.ValueKind == JsonValueKind.Object &&
                            ReadBool(typeElement, "is_channel")
            };
        }
        catch (Exception exception)
        {
            _logger.Warning($"Telegram sohbeti okunamadı ({chatId}): {exception.Message}");
            return null;
        }
    }

    private async Task<string> GetChatTitleAsync(long chatId, CancellationToken cancellationToken)
    {
        if (_chatTitles.TryGetValue(chatId, out var title)) return title;
        var chat = await GetChatAsync(chatId, cancellationToken).ConfigureAwait(false);
        return chat?.Title ?? "Telegram sohbeti";
    }

    private static void AddMessages(IDictionary<string, JsonElement> target, JsonElement response)
    {
        if (!response.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return;
        TelegramForumMessageMerge.Add(target, messages.EnumerateArray());
    }

    private TelegramMediaItem? ParseMedia(JsonElement message, string chatTitle)
    {
        if (!message.TryGetProperty("content", out var content)) return null;
        var contentType = ReadString(content, "@type");
        JsonElement media;
        JsonElement file;
        TelegramMediaKind kind;
        string name;
        string mime;
        int duration;

        switch (contentType)
        {
            case "messageVideo" when content.TryGetProperty("video", out media) && media.TryGetProperty("video", out file):
                kind = TelegramMediaKind.Video;
                name = ReadString(media, "file_name", "telegram-video.mp4");
                mime = ReadString(media, "mime_type", "video/mp4");
                duration = ReadInt32(media, "duration");
                break;
            case "messageAnimation" when content.TryGetProperty("animation", out media) && media.TryGetProperty("animation", out file):
                kind = TelegramMediaKind.Animation;
                name = ReadString(media, "file_name", "telegram-animation.mp4");
                mime = ReadString(media, "mime_type", "video/mp4");
                duration = ReadInt32(media, "duration");
                break;
            case "messageDocument" when content.TryGetProperty("document", out media) && media.TryGetProperty("document", out file):
                name = ReadString(media, "file_name", "telegram-dosya");
                mime = ReadString(media, "mime_type", "application/octet-stream");
                kind = ClassifyDocumentKind(name, mime);
                duration = 0;
                break;
            case "messageAudio" when content.TryGetProperty("audio", out media) && media.TryGetProperty("audio", out file):
                kind = TelegramMediaKind.Audio;
                name = ReadString(media, "file_name", "telegram-audio");
                mime = ReadString(media, "mime_type", "audio/mpeg");
                duration = ReadInt32(media, "duration");
                break;
            default:
                return null;
        }

        var fileInfo = ParseFile(file);
        UpdateFileState(fileInfo);
        var caption = content.TryGetProperty("caption", out var captionElement)
            ? ReadString(captionElement, "text")
            : string.Empty;
        var dateUnix = ReadInt64(message, "date");
        var date = dateUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(dateUnix) : DateTimeOffset.MinValue;
        return new TelegramMediaItem(
            ReadInt64(message, "chat_id"),
            ReadInt64(message, "id"),
            fileInfo.Id,
            chatTitle,
            name,
            caption,
            mime,
            fileInfo.Size,
            duration,
            date,
            kind,
            fileInfo.Path,
            fileInfo.IsCompleted);
    }

    // Never use Telegram message captions to identify or exclude documents.
    // Files such as Film.mkv.001 are legitimate Telegram attachments even
    // when not recognized by a normal video MIME/extension.
    private static TelegramMediaKind ClassifyDocumentKind(string name, string mime) =>
        TelegramArchiveSetDetector.IsArchivePartFileName(name) ? TelegramMediaKind.ArchivePart :
        IsAudioDocument(name, mime) ? TelegramMediaKind.Audio :
        IsSubtitleDocument(name) ? TelegramMediaKind.Subtitle :
        IsPlayableDocument(name, mime) ? TelegramMediaKind.Video :
        TelegramMediaKind.Document;

    private static bool IsAudioDocument(string name, string mime)
    {
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return true;

        var extension = Path.GetExtension(name);
        return new[]
        {
            ".mka", ".mp3", ".flac", ".aac", ".ac3", ".eac3", ".dts",
            ".dtshd", ".thd", ".truehd", ".m4a", ".ogg", ".opus", ".wav"
        }.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSubtitleDocument(string name) =>
        ArchiveMediaAttachmentExtensions.Contains(Path.GetExtension(name));

    private static readonly HashSet<string> ArchiveMediaAttachmentExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup"
        };

    private static bool IsPlayableDocument(string name, string mime)
    {
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || IsAudioDocument(name, mime))
            return true;

        var extension = Path.GetExtension(name);
        return new[]
        {
            ".mkv", ".mp4", ".m4v", ".webm", ".mov", ".avi", ".ts", ".m2ts", ".mpg", ".mpeg"
        }.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_native is null) return;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var json = _native.Receive(0.25);
                if (string.IsNullOrWhiteSpace(json)) continue;
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement.Clone();
                HandleIncoming(root);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.Error("TDLib receive döngüsünde hata oluştu.", exception);
                SetAuthorization(new(TelegramAuthorizationStage.Error, exception.Message));
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void HandleIncoming(JsonElement root)
    {
        if (root.TryGetProperty("@extra", out var extraElement))
        {
            var extra = extraElement.ValueKind == JsonValueKind.String ? extraElement.GetString() : extraElement.ToString();
            if (!string.IsNullOrWhiteSpace(extra) && _pending.TryRemove(extra, out var completion))
            {
                if (ReadString(root, "@type") == "error")
                    completion.TrySetException(new TelegramRequestException(ReadInt32(root, "code"), ReadString(root, "message", "TDLib hatası")));
                else completion.TrySetResult(root);
                return;
            }
        }

        switch (ReadString(root, "@type"))
        {
            case "updateAuthorizationState" when root.TryGetProperty("authorization_state", out var state):
                _ = ProcessAuthorizationStateAsync(state.Clone(), _lifetime.Token);
                break;
            case "updateFile" when root.TryGetProperty("file", out var file):
                HandleFileUpdate(file);
                break;
            case "updateChatTitle":
                var chatId = ReadInt64(root, "chat_id");
                if (chatId != 0) _chatTitles[chatId] = ReadString(root, "title", "Telegram sohbeti");
                break;
            case "updateChatFolders":
                ApplyChatFolders(root);
                break;
        }
    }

    private void ApplyChatFolders(JsonElement update)
    {
        var folders = new List<TelegramChatFolderItem>();
        var mainPosition = ReadInt32(update, "main_chat_list_position");
        folders.Add(new TelegramChatFolderItem(null, "Telegram.AllChats", mainPosition));
        if (update.TryGetProperty("chat_folders", out var rawFolders) && rawFolders.ValueKind == JsonValueKind.Array)
        {
            var position = 0;
            foreach (var folder in rawFolders.EnumerateArray())
            {
                var id = ReadInt32(folder, "id");
                if (id <= 0) continue;
                var name = $"Klasör {id}";
                if (folder.TryGetProperty("name", out var rawName) && rawName.TryGetProperty("text", out var formattedText))
                {
                    name = formattedText.ValueKind == JsonValueKind.String
                        ? formattedText.GetString() ?? name
                        : ReadString(formattedText, "text", name);
                }
                folders.Add(new TelegramChatFolderItem(id, name, position++));
            }
        }
        folders.Add(new TelegramChatFolderItem(-1, "Telegram.Archive", int.MaxValue));
        _chatFolders = folders.OrderBy(item => item.Position).ToArray();
        ChatFoldersChanged?.Invoke(this, _chatFolders);
    }

    private async Task ProcessAuthorizationStateAsync(JsonElement state, CancellationToken cancellationToken)
    {
        var stateType = ReadString(state, "@type");
        switch (stateType)
        {
            case "authorizationStateWaitTdlibParameters":
                SetAuthorization(new(TelegramAuthorizationStage.WaitingForParameters, "TDLib parametreleri ayarlanıyor…"));
                if (Interlocked.Exchange(ref _parametersSent, 1) == 0) await SendParametersAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "authorizationStateWaitPhoneNumber":
                SetAuthorization(new(TelegramAuthorizationStage.WaitingForPhoneNumber, "Telefon numaranızı uluslararası biçimde girin."));
                break;
            case "authorizationStateWaitEmailAddress":
                SetAuthorization(new(TelegramAuthorizationStage.WaitingForEmailAddress, "Telegram e-posta adresi bekliyor."));
                break;
            case "authorizationStateWaitEmailCode":
                SetAuthorization(new(TelegramAuthorizationStage.WaitingForEmailCode, "E-posta doğrulama kodunu girin."));
                break;
            case "authorizationStateWaitCode":
                SetAuthorization(new(TelegramAuthorizationStage.WaitingForCode, "Telegram doğrulama kodunu girin."));
                break;
            case "authorizationStateWaitPassword":
                SetAuthorization(new(TelegramAuthorizationStage.WaitingForPassword, "İki aşamalı doğrulama parolanızı girin."));
                break;
            case "authorizationStateWaitRegistration":
                SetAuthorization(new(TelegramAuthorizationStage.WaitingForRegistration, "Yeni Telegram hesabı için ad ve soyad girin."));
                break;
            case "authorizationStateWaitOtherDeviceConfirmation":
                SetAuthorization(new(TelegramAuthorizationStage.WaitingForOtherDeviceConfirmation,
                    $"Diğer cihazınızdan Telegram girişini onaylayın: {ReadString(state, "link")}"));
                break;
            case "authorizationStateWaitPremiumPurchase":
                SetAuthorization(new(TelegramAuthorizationStage.Error, "Bu hesap için Telegram Premium doğrulaması gerekiyor."));
                break;
            case "authorizationStateReady":
                SetAuthorization(new(TelegramAuthorizationStage.Ready, "Telegram hesabı bağlı.", true));
                break;
            case "authorizationStateLoggingOut":
                SetAuthorization(new(TelegramAuthorizationStage.LoggingOut, "Telegram oturumu kapatılıyor…"));
                break;
            case "authorizationStateClosing":
                SetAuthorization(new(TelegramAuthorizationStage.Closing, "TDLib kapatılıyor…"));
                break;
            case "authorizationStateClosed":
                SetAuthorization(new(TelegramAuthorizationStage.Closed, "Telegram oturumu kapandı."));
                break;
            default:
                SetAuthorization(new(TelegramAuthorizationStage.Starting, stateType));
                break;
        }
    }

    private Task SendParametersAsync(CancellationToken cancellationToken)
    {
        var configuration = _configuration ?? throw new InvalidOperationException("Telegram yapılandırması bulunamadı.");
        return RequestVoidAsync(new Dictionary<string, object?>
        {
            ["@type"] = "setTdlibParameters",
            ["use_test_dc"] = false,
            ["database_directory"] = configuration.DatabaseDirectory,
            ["files_directory"] = configuration.FilesDirectory,
            ["database_encryption_key"] = configuration.DatabaseEncryptionKey,
            ["use_file_database"] = true,
            ["use_chat_info_database"] = true,
            ["use_message_database"] = true,
            ["use_secret_chats"] = false,
            ["api_id"] = configuration.ApiId,
            ["api_hash"] = configuration.ApiHash,
            ["system_language_code"] = "tr-TR",
            ["device_model"] = configuration.DeviceModel,
            ["system_version"] = Environment.OSVersion.VersionString,
            ["application_version"] = configuration.ApplicationVersion
        }, cancellationToken);
    }

    private void HandleFileUpdate(JsonElement file)
    {
        var snapshot = ParseFile(file);
        UpdateFileState(snapshot);
        DownloadProgressChanged?.Invoke(this, new TelegramDownloadProgress(
            snapshot.Id,
            snapshot.Downloaded,
            snapshot.Size,
            snapshot.IsCompleted,
            snapshot.Path));
        if (snapshot.IsCompleted && !string.IsNullOrWhiteSpace(snapshot.Path) && _downloads.TryGetValue(snapshot.Id, out var completion))
            completion.TrySetResult(snapshot.Path);
    }

    private void UpdateFileState(TelegramFileSnapshot snapshot)
    {
        if (snapshot.Id <= 0) return;
        _fileStates[snapshot.Id] = snapshot;
        _filePulses.GetOrAdd(snapshot.Id, _ => new AsyncPulse()).Pulse();
    }

    private async Task RequestVoidAsync(Dictionary<string, object?> request, CancellationToken cancellationToken)
    {
        _ = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private Task<JsonElement> RequestAsync(
        Dictionary<string, object?> request,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ThrowIfDisposed();
        if (_native is null || _clientId == 0) throw new InvalidOperationException("TDLib başlatılmadı.");
        var extra = $"adb-w4-{Interlocked.Increment(ref _requestSequence)}-{Guid.NewGuid():N}";
        request["@extra"] = extra;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(extra, completion)) throw new InvalidOperationException("TDLib istek kimliği oluşturulamadı.");
        try
        {
            _native.Send(_clientId, JsonSerializer.Serialize(request));
        }
        catch
        {
            _pending.TryRemove(extra, out _);
            throw;
        }
        return AwaitResponseAsync(extra, completion.Task, timeout ?? TimeSpan.FromSeconds(30), cancellationToken);
    }

    private async Task<JsonElement> AwaitResponseAsync(string extra, Task<JsonElement> task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        finally { _pending.TryRemove(extra, out _); }
    }

    private void ExecuteBestEffort(Dictionary<string, object?> request)
    {
        try { _native?.Execute(JsonSerializer.Serialize(request)); }
        catch (Exception exception) { _logger.Warning($"TDLib senkron ayarı uygulanamadı: {exception.Message}"); }
    }

    private void EnsureReady()
    {
        if (!Authorization.IsReady) throw new InvalidOperationException("Telegram hesabı henüz kullanıma hazır değil.");
    }

    private void SetAuthorization(TelegramAuthorizationSnapshot snapshot)
    {
        Authorization = snapshot;
        AuthorizationChanged?.Invoke(this, snapshot);
        _logger.Info($"Telegram yetkilendirme: {snapshot.Stage} - {snapshot.Message}");
    }

    private static TelegramFileSnapshot ParseFile(JsonElement file)
    {
        var id = ReadInt32(file, "id");
        var size = Math.Max(ReadInt64(file, "size"), ReadInt64(file, "expected_size"));
        var downloaded = 0L;
        var path = string.Empty;
        var completed = false;
        var downloadOffset = 0L;
        var downloadedPrefix = 0L;
        if (file.TryGetProperty("local", out var local))
        {
            downloaded = ReadInt64(local, "downloaded_size");
            downloadOffset = ReadInt64(local, "download_offset");
            downloadedPrefix = ReadInt64(local, "downloaded_prefix_size");
            path = ReadString(local, "path");
            completed = ReadBool(local, "is_downloading_completed");
        }
        return new TelegramFileSnapshot(id, size, downloaded, downloadOffset, downloadedPrefix, path, completed);
    }

    private static long ParseInt64(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var value) => value,
        JsonValueKind.String when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
        _ => 0
    };

    private static long ReadInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? ParseInt64(value) : 0;

    private static int ReadInt32(JsonElement element, string name)
    {
        var value = ReadInt64(element, name);
        return value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
    }

    private static string ReadString(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_native is not null && _clientId != 0 && Authorization.Stage is not TelegramAuthorizationStage.Closed)
            {
                try { _native.Send(_clientId, JsonSerializer.Serialize(new Dictionary<string, object?> { ["@type"] = "close" })); }
                catch { }
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifetime.Cancel();
            if (_receiveTask is not null)
            {
                try { await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { }
            }
            foreach (var pending in _pending.Values) pending.TrySetCanceled();
            foreach (var download in _downloads.Values) download.TrySetCanceled();
            if (_streamServer is not null) await _streamServer.DisposeAsync().ConfigureAwait(false);
            foreach (var session in _archiveCacheSessions.Values)
                await session.DisposeAsync().ConfigureAwait(false);
            _archiveCacheSessions.Clear();
            foreach (var gate in _rangeRequestGates.Values) gate.Dispose();
            // Do not dispose the lightweight optimizer gate while a late UI cache task may release it.
            _native?.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed class TelegramRequestException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private sealed record TelegramFileSnapshot(
        int Id,
        long Size,
        long Downloaded,
        long DownloadOffset,
        long DownloadedPrefix,
        string Path,
        bool IsCompleted);

    private sealed class AsyncPulse
    {
        private readonly object _gate = new();
        private TaskCompletionSource<bool> _next = NewCompletion();

        public Task Next
        {
            get { lock (_gate) return _next.Task; }
        }

        public void Pulse()
        {
            TaskCompletionSource<bool> completion;
            lock (_gate)
            {
                completion = _next;
                _next = NewCompletion();
            }
            completion.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

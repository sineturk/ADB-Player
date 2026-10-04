#!/usr/bin/env python3
"""Headless bridge between AltyazıDB Player and AudioSyncTool v2.5.0.

Modes:
  * analysis: serialize the upstream AudioAnalyzer result
  * full sync: run the upstream SyncPipeline
  * multi-modal timeline sync: when upstream full sync is doubtful, build a
    monotonic source->target timeline using waveform fingerprints and banded DTW.
  * visual-assisted timeline sync: when the dub source also contains video,
    combine visual edit anchors with waveform micro-alignment.
  * legacy hard-cut recovery remains the final fallback.

Multi-Modal Timeline Sync is deliberately a fallback. Normal files continue
through upstream AudioSyncTool unchanged. Visual input is optional; audio-only
AC3/EAC3/AAC/FLAC/WAV/MKA sources remain first-class inputs. Missing target scenes
use the reference media's original audio; no audio is invented.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path
from types import SimpleNamespace

WORKER_VERSION = "3.2.0"
AUDIOSYNCTOOL_VERSION = "2.5.0"
AUDIOSYNCTOOL_COMMIT = "b90fba80b182dae14fc72d5af02d7504794a515c"
HYBRID_FEATURE_RATE = 5.0
HYBRID_PCM_RATE = 8000
VISUAL_SAMPLE_SEC = 2.0
VISUAL_WIDTH = 32
VISUAL_HEIGHT = 16


def _bootstrap_source() -> None:
    here = Path(__file__).resolve().parent
    candidates = [
        here / "source",
        here.parent.parent.parent / "native" / "audiosynctool" / "source",
    ]
    for candidate in candidates:
        if (candidate / "audio_sync").is_dir():
            sys.path.insert(0, str(candidate))
            return


def _json_safe(value):
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return value


def _emit(payload: dict) -> None:
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def _emit_progress(value: int, message: str | None = None) -> None:
    payload = {"event": "progress", "progress": int(max(0, min(100, value)))}
    if message:
        payload["message"] = message
    _emit(payload)


def _emit_log(message: str) -> None:
    text = str(message).replace("\r", " ").replace("\n", " ").strip()
    if text:
        _emit({"event": "log", "message": text[:500]})


def _region_to_dict(region, duration_sec: float) -> dict:
    end = float(getattr(region, "end_sec", duration_sec))
    if not math.isfinite(end):
        end = duration_sec
    return {
        "start_sec": float(getattr(region, "start_sec", 0.0)),
        "end_sec": float(end),
        "lag_ms": float(getattr(region, "lag_ms", 0.0)),
        "window_count": int(getattr(region, "window_count", 0)),
        "confidence": float(getattr(region, "confidence", 0.0)),
    }


def _result_to_dict(result, duration_sec: float) -> dict:
    verdict = getattr(getattr(result, "verdict", None), "value", "uncertain")
    regions = [
        _region_to_dict(region, duration_sec)
        for region in (getattr(result, "offset_regions", ()) or ())
    ]
    return {
        "ok": True,
        "engine": "AudioSyncTool",
        "engine_version": AUDIOSYNCTOOL_VERSION,
        "worker_version": WORKER_VERSION,
        "commit": AUDIOSYNCTOOL_COMMIT,
        "verdict": str(verdict),
        "delay_ms": float(getattr(result, "delay_ms", 0.0)),
        "coarse_ms": float(getattr(result, "coarse_ms", 0.0)),
        "confidence": float(getattr(result, "confidence", 0.0)),
        "used_segments": int(getattr(result, "used_segments", 0)),
        "total_segments": int(getattr(result, "total_segments", 0)),
        "drift_ms_per_min": _json_safe(getattr(result, "drift_ms_per_min", None)),
        "drift_intercept_ms": _json_safe(getattr(result, "drift_intercept_ms", None)),
        "drift_r2": _json_safe(getattr(result, "drift_r2", None)),
        "drift_span_sec": float(getattr(result, "drift_span_sec", 0.0) or 0.0),
        "lag_spread_ms": float(getattr(result, "lag_spread_ms", 0.0) or 0.0),
        "suspected_fps_conversion": getattr(result, "suspected_fps_conversion", None),
        "phat_refined_ms": _json_safe(getattr(result, "phat_refined_ms", None)),
        "phat_sharpness": float(getattr(result, "phat_sharpness", 0.0) or 0.0),
        "phat_probes": int(getattr(result, "phat_probes", 0) or 0),
        "verdict_reasons": list(getattr(result, "verdict_reasons", ()) or ()),
        "regions": regions,
    }


def _self_test() -> int:
    _bootstrap_source()
    try:
        import numpy as np
        import scipy
        from audio_sync.core.analyzer import AudioAnalyzer
        from audio_sync.core.pipeline import SyncPipeline
        _ = (AudioAnalyzer, SyncPipeline, np)

        # Exercise the Rev10.8 banded-DTW core with a deterministic synthetic
        # timeline. This catches packaging/runtime regressions before installation.
        rng = np.random.default_rng(1080)
        ext_test = rng.normal(size=(220, 8)).astype(np.float32)
        ref_test = rng.normal(scale=0.05, size=(260, 8)).astype(np.float32)
        shift_frames = 20  # 4 seconds at 5 Hz.
        ref_test[shift_frames:shift_frames + len(ext_test)] = ext_test
        dtw_test = _banded_dtw_refine(
            ref_test, ext_test, 4.0, 34.0, 1.0, 4.0, band_sec=3.0
        )
        if dtw_test is None or abs(float(dtw_test["intercept"]) - 4.0) > 1.0:
            raise RuntimeError("Multi-Modal banded DTW self-test başarısız.")

        live_test = _live_refine_mapping_from_observations(
            1.0, 100.0, 60.0,
            [(12.0, 0.42), (30.0, 0.40), (48.0, 0.41)],
        )
        if live_test is None or not (99.3 < float(live_test["source_start_sec"]) < 99.9):
            raise RuntimeError("Live Progressive lokal kalibrasyon self-test başarısız.")

        _emit({
            "ok": True,
            "engine": "AudioSyncTool",
            "engine_version": AUDIOSYNCTOOL_VERSION,
            "worker_version": WORKER_VERSION,
            "commit": AUDIOSYNCTOOL_COMMIT,
            "python": sys.version.split()[0],
            "numpy": np.__version__,
            "scipy": scipy.__version__,
            "full_sync": True,
            "hybrid_edit_recovery": True,
            "visual_cut_recovery": True,
            "windows_pcm_lock_fix": True,
            "waveform_timeline_mapper": True,
            "banded_dtw": True,
            "multimodal_timeline_sync": True,
            "audio_first_visual_optional": True,
            "visual_waveform_guard": True,
            "post_render_waveform_calibration": True,
            "progressive_adaptive_sync": True,
            "segment_verified_fallback": True,
            "normal_ast_fast_path_unchanged": True,
            "live_progressive_sync": True,
            "background_lookahead": True,
            "seek_aware_live_sync": True,
            "persistent_live_region_cache": True,
        })
        return 0
    except Exception as exc:
        _emit({"ok": False, "error": str(exc)})
        traceback.print_exc(file=sys.stderr)
        return 2


def _analyze(args: argparse.Namespace) -> int:
    _bootstrap_source()
    try:
        from audio_sync.core.analyzer import AudioAnalyzer

        reference = Path(args.reference).resolve()
        external = Path(args.external).resolve()
        if not reference.is_file() or not external.is_file():
            raise FileNotFoundError("PCM analiz dosyalarından biri bulunamadı.")

        duration_sec = min(reference.stat().st_size, external.stat().st_size) / 2.0 / float(args.rate)
        analyzer = AudioAnalyzer()
        result = analyzer.calculate_delay_from_pcm_files(
            int(args.rate),
            str(reference),
            str(external),
            sync_rate=int(args.rate),
            skip_intro_sec=float(args.skip_intro),
            total_segments=int(args.segments),
        )
        _emit(_result_to_dict(result, duration_sec))
        return 0
    except Exception as exc:
        _emit({
            "ok": False,
            "engine": "AudioSyncTool",
            "engine_version": AUDIOSYNCTOOL_VERSION,
            "error": str(exc),
            "error_type": type(exc).__name__,
        })
        traceback.print_exc(file=sys.stderr)
        return 1


def _run_checked(command: list[str]) -> subprocess.CompletedProcess:
    completed = subprocess.run(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip() or "FFmpeg işlemi başarısız oldu."
        raise RuntimeError(detail[-1200:])
    return completed


def _probe_audio(path: Path) -> tuple[int, int]:
    result = _run_checked([
        "ffprobe", "-v", "error", "-select_streams", "a:0",
        "-show_entries", "stream=channels,sample_rate", "-of", "json", str(path),
    ])
    payload = json.loads(result.stdout or "{}")
    streams = payload.get("streams") or []
    if not streams:
        return (2, 48000)
    stream = streams[0]
    channels = max(1, min(8, int(stream.get("channels") or 2)))
    sample_rate = int(stream.get("sample_rate") or 48000)
    if sample_rate < 8000 or sample_rate > 192000:
        sample_rate = 48000
    return channels, sample_rate


def _decode_pcm(media: Path, output: Path, rate: int) -> None:
    _run_checked([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-i", str(media), "-map", "0:a:0", "-vn", "-sn", "-dn",
        "-ac", "1", "-ar", str(rate), "-c:a", "pcm_s16le", "-f", "s16le", str(output),
    ])
    if not output.is_file() or output.stat().st_size < rate * 2 * 20:
        raise RuntimeError("Hybrid Recovery için yeterli PCM üretilemedi.")


def _probe_video_duration(path: Path) -> float | None:
    try:
        result = _run_checked([
            "ffprobe", "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=index:format=duration", "-of", "json", str(path),
        ])
        payload = json.loads(result.stdout or "{}")
        if not (payload.get("streams") or []):
            return None
        duration = float((payload.get("format") or {}).get("duration") or 0.0)
        return duration if math.isfinite(duration) and duration > 20.0 else None
    except Exception:
        return None


def _visual_descriptors(media: Path, sample_sec: float = VISUAL_SAMPLE_SEC):
    """Low-resolution, subtitle-tolerant visual fingerprints.

    Only the central 72% of the picture is used so burned-in subtitles, letterbox
    text and channel bugs have less influence. Three adjacent samples are joined
    into one descriptor to make static/repeated shots less ambiguous.
    """
    import numpy as np

    vf = (
        f"fps=1/{sample_sec:.6f},"
        "crop=iw:ih*0.72:0:ih*0.08,"
        f"scale={VISUAL_WIDTH}:{VISUAL_HEIGHT}:flags=area,format=gray"
    )
    completed = subprocess.run(
        [
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", str(media), "-an", "-sn", "-dn", "-vf", vf,
            "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1",
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if completed.returncode != 0:
        detail = completed.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(detail[-1000:] or "Görsel kare örnekleri çıkarılamadı.")

    frame_size = VISUAL_WIDTH * VISUAL_HEIGHT
    raw = np.frombuffer(completed.stdout, dtype=np.uint8)
    count = int(len(raw) // frame_size)
    if count < 20:
        raise RuntimeError("Görsel Recovery için yeterli kare örneği üretilemedi.")
    frames = raw[:count * frame_size].reshape(count, VISUAL_HEIGHT, VISUAL_WIDTH).astype(np.float32)

    # Normalize luminance per frame so SDR/HDR/encode brightness differences do
    # not dominate. Include simple gradients to emphasise geometry over colour.
    central = frames / 255.0
    mean = np.mean(central, axis=(1, 2), keepdims=True)
    std = np.std(central, axis=(1, 2), keepdims=True)
    norm = (central - mean) / np.maximum(std, 0.035)
    gx = np.diff(norm, axis=2, append=norm[:, :, -1:])
    gy = np.diff(norm, axis=1, append=norm[:, -1:, :])
    single = np.concatenate(
        [norm.reshape(count, -1), gx.reshape(count, -1), gy.reshape(count, -1)], axis=1
    )
    single_norm = np.linalg.norm(single, axis=1, keepdims=True)
    single = single / np.maximum(single_norm, 1e-6)

    # Three-frame temporal context: t-2s / t / t+2s.
    desc = np.zeros((count, single.shape[1] * 3), dtype=np.float32)
    for i in range(count):
        a = single[max(0, i - 1)]
        b = single[i]
        c = single[min(count - 1, i + 1)]
        merged = np.concatenate([a, b, c]).astype(np.float32, copy=False)
        desc[i] = merged / max(float(np.linalg.norm(merged)), 1e-6)
    return desc


def _visual_fit(pairs: list[tuple[float, float, float]]):
    import numpy as np
    if len(pairs) < 3:
        return None
    arr = np.asarray(pairs, dtype=np.float64)
    x, y, score = arr[:, 0], arr[:, 1], arr[:, 2]
    mask = np.ones(len(arr), dtype=bool)
    for _ in range(5):
        if np.count_nonzero(mask) < 3:
            return None
        rate, intercept = np.polyfit(x[mask], y[mask], 1)
        if not (0.84 <= rate <= 1.16):
            return None
        residual = y - (rate * x + intercept)
        active = np.abs(residual[mask])
        med = float(np.median(active)) if len(active) else 99.0
        mad = float(np.median(np.abs(active - med))) if len(active) else 99.0
        limit = min(5.5, max(2.4, med + 3.0 * max(mad, 0.35)))
        new_mask = np.abs(residual) <= limit
        if np.array_equal(mask, new_mask):
            break
        mask = new_mask
    if np.count_nonzero(mask) < 3:
        return None
    rate, intercept = np.polyfit(x[mask], y[mask], 1)
    residual = y[mask] - (rate * x[mask] + intercept)
    median_residual = float(np.median(np.abs(residual))) if len(residual) else 99.0
    mean_score = float(np.mean(np.clip(score[mask], 0.0, 1.0)))
    count_factor = min(1.0, np.count_nonzero(mask) / 8.0)
    confidence = mean_score * count_factor * math.exp(-median_residual / 3.0)
    return {
        "rate": float(rate),
        "intercept": float(intercept),
        "confidence": float(max(0.0, min(1.0, confidence))),
        "median_residual_sec": median_residual,
        "anchors": int(np.count_nonzero(mask)),
        "pairs": [(float(a), float(b), float(c)) for a, b, c in arr[mask]],
    }


def _visual_recursive_segments(pairs: list[tuple[float, float, float]], depth: int = 0):
    import numpy as np
    if len(pairs) < 5 or depth >= 8:
        fit = _visual_fit(pairs)
        return [(pairs, fit)] if fit else []
    fit = _visual_fit(pairs)
    if fit is None:
        mid = len(pairs) // 2
        return _visual_recursive_segments(pairs[:mid], depth + 1) + _visual_recursive_segments(pairs[mid:], depth + 1)
    x = np.asarray([p[0] for p in pairs], dtype=np.float64)
    y = np.asarray([p[1] for p in pairs], dtype=np.float64)
    residual = np.abs(y - (fit["rate"] * x + fit["intercept"]))
    worst = int(np.argmax(residual))
    # A real edit creates a multi-second discontinuity. Keep encoder/frame sample
    # noise inside one segment and split only on a clear jump.
    if float(residual[worst]) <= 5.0 or worst < 3 or worst > len(pairs) - 4:
        return [(pairs, fit)]
    return _visual_recursive_segments(pairs[:worst], depth + 1) + _visual_recursive_segments(pairs[worst:], depth + 1)


def _monotonic_visual_path(pairs: list[tuple[float, float, float]]):
    """Weighted monotonic chain that tolerates inserted/deleted scenes."""
    if not pairs:
        return []
    pairs = sorted(pairs, key=lambda item: (item[0], item[1]))
    n = len(pairs)
    score = [0.0] * n
    parent = [-1] * n
    for i, (sx, ty, quality) in enumerate(pairs):
        score[i] = 1.0 + quality * quality
        for j in range(i):
            ps, pt, _pq = pairs[j]
            ds = sx - ps
            dt = ty - pt
            if ds <= 0.0 or dt <= 0.0:
                continue
            # Normal 24/25 changes fit inside 0.84..1.16. Added/removed scenes
            # are allowed as positive jumps, but wildly unrelated matches are not.
            if dt < ds * 0.70 - 20.0:
                continue
            if dt > ds * 1.35 + 210.0:
                continue
            candidate = score[j] + 1.0 + quality * quality
            if candidate > score[i]:
                score[i] = candidate
                parent[i] = j
    index = max(range(n), key=lambda idx: score[idx])
    chain = []
    while index >= 0:
        chain.append(pairs[index])
        index = parent[index]
    chain.reverse()
    return chain


def _visual_plan(reference: Path, external: Path, analysis) -> dict:
    import numpy as np

    ref_duration = _probe_video_duration(reference)
    ext_duration = _probe_video_duration(external)
    if ref_duration is None or ext_duration is None:
        return {"usable": False, "reason": "video_stream_missing", "regions": []}

    ref_desc = _visual_descriptors(reference)
    ext_desc = _visual_descriptors(external)
    ref_count = len(ref_desc)
    ext_count = len(ext_desc)
    if ref_count < 30 or ext_count < 30:
        return {"usable": False, "reason": "visual_samples_short", "regions": []}

    base_delay = float(getattr(analysis, "delay_ms", 0.0) or 0.0) / 1000.0 if analysis is not None else 0.0
    duration_ratio = ref_duration / max(ext_duration, 1.0)
    candidates: list[tuple[float, float, float]] = []

    # One source anchor every ~10 s. Search a broad local window because a
    # 25->24 speed change can accumulate ~4 minutes over a feature film and edits
    # can add further jumps.
    anchor_step = max(1, int(round(10.0 / VISUAL_SAMPLE_SEC)))
    search_radius_frames = max(30, int(round(360.0 / VISUAL_SAMPLE_SEC)))
    for ext_index in range(anchor_step * 2, ext_count - anchor_step * 2, anchor_step):
        ext_time = ext_index * VISUAL_SAMPLE_SEC
        predicted = ext_time * duration_ratio + base_delay
        if analysis is not None:
            for region in (getattr(analysis, "offset_regions", ()) or ()):
                target_start = float(getattr(region, "start_sec", 0.0) or 0.0)
                target_end = float(getattr(region, "end_sec", ref_duration) or ref_duration)
                if not math.isfinite(target_end):
                    target_end = ref_duration
                lag = float(getattr(region, "lag_ms", base_delay * 1000.0) or 0.0) / 1000.0
                source_start = target_start - lag
                source_end = target_end - lag
                if source_start - 45.0 <= ext_time <= source_end + 45.0:
                    predicted = ext_time + lag
                    break
        center = int(round(predicted / VISUAL_SAMPLE_SEC))
        lo = max(1, center - search_radius_frames)
        hi = min(ref_count - 1, center + search_radius_frames + 1)
        if hi - lo < 8:
            continue
        block = ref_desc[lo:hi]
        scores = block @ ext_desc[ext_index]
        best_local = int(np.argmax(scores))
        best = float(scores[best_local])
        if len(scores) > 12:
            masked = scores.copy()
            a = max(0, best_local - 2)
            b = min(len(masked), best_local + 3)
            masked[a:b] = -2.0
            second = float(np.max(masked))
        else:
            second = -1.0
        margin = best - second
        if best < 0.70 or margin < 0.008:
            continue
        ref_index = lo + best_local
        ref_time = ref_index * VISUAL_SAMPLE_SEC
        candidates.append((ext_time, ref_time, min(1.0, max(0.0, best))))

    chain = _monotonic_visual_path(candidates)
    if len(chain) < 10:
        return {
            "usable": False, "reason": "visual_anchor_floor", "regions": [],
            "anchors": len(chain), "coverage": 0.0, "confidence": 0.0,
        }

    segments = _visual_recursive_segments(chain)
    good = [(pairs, fit) for pairs, fit in segments if fit and fit["confidence"] >= 0.11 and fit["anchors"] >= 3]
    if not good:
        return {
            "usable": False, "reason": "visual_fit_floor", "regions": [],
            "anchors": len(chain), "coverage": 0.0, "confidence": 0.0,
        }

    plan_regions: list[dict] = []
    all_conf = []
    total_anchors = 0
    for idx, (pairs, fit) in enumerate(good):
        first_source = float(pairs[0][0])
        last_source = float(pairs[-1][0])
        source_start = max(0.0, first_source - 5.0)
        source_end = min(ext_duration, last_source + 5.0)
        if source_end - source_start < 8.0:
            continue
        target_start = max(0.0, fit["rate"] * source_start + fit["intercept"])
        target_end = min(ref_duration, fit["rate"] * source_end + fit["intercept"])
        if target_end - target_start < 6.0:
            continue
        plan_regions.append({
            "target_start_sec": target_start,
            "target_end_sec": target_end,
            "source_start_sec": source_start,
            "source_end_sec": source_end,
            "rate": float(fit["rate"]),
            "confidence": float(fit["confidence"]),
            "missing_source": False,
            "anchor_count": int(fit["anchors"]),
            "residual_ms": float(fit["median_residual_sec"] * 1000.0),
        })
        all_conf.append(float(fit["confidence"]))
        total_anchors += int(fit["anchors"])

    plan_regions.sort(key=lambda item: item["target_start_sec"])
    # Explicitly mark target gaps as original/reference audio. This also makes the
    # missing-scene count visible in the Player diagnostics.
    completed_regions: list[dict] = []
    cursor = 0.0
    missing = 0
    for region in plan_regions:
        start = max(cursor, float(region["target_start_sec"]))
        end = float(region["target_end_sec"])
        if start > cursor + 0.40:
            completed_regions.append({
                "target_start_sec": cursor,
                "target_end_sec": start,
                "source_start_sec": 0.0,
                "source_end_sec": 0.0,
                "rate": 1.0,
                "confidence": 0.0,
                "missing_source": True,
                "anchor_count": 0,
                "residual_ms": None,
            })
            missing += 1
        if end > start + 0.20:
            adjusted = dict(region)
            if start > float(region["target_start_sec"]):
                shift = (start - float(region["target_start_sec"])) / max(float(region["rate"]), 1e-6)
                adjusted["source_start_sec"] = float(region["source_start_sec"]) + shift
                adjusted["target_start_sec"] = start
            completed_regions.append(adjusted)
            cursor = max(cursor, end)
    if cursor < ref_duration - 0.40:
        completed_regions.append({
            "target_start_sec": cursor,
            "target_end_sec": ref_duration,
            "source_start_sec": 0.0,
            "source_end_sec": 0.0,
            "rate": 1.0,
            "confidence": 0.0,
            "missing_source": True,
            "anchor_count": 0,
            "residual_ms": None,
        })
        missing += 1

    matched_duration = sum(
        max(0.0, float(r["target_end_sec"]) - float(r["target_start_sec"]))
        for r in completed_regions if not r.get("missing_source")
    )
    coverage = min(1.0, matched_duration / max(ref_duration, 1.0))
    confidence = float(np.mean(all_conf)) if all_conf else 0.0
    source_span = chain[-1][0] - chain[0][0] if len(chain) >= 2 else 0.0
    span_ratio = source_span / max(ext_duration, 1.0)
    usable = (
        total_anchors >= 12 and
        coverage >= 0.55 and
        confidence >= 0.10 and
        span_ratio >= 0.45
    )
    return {
        "usable": bool(usable),
        "reason": "visual_ok" if usable else "visual_quality_floor",
        "reference_duration_sec": ref_duration,
        "external_duration_sec": ext_duration,
        "coverage": coverage,
        "confidence": confidence,
        "anchors": total_anchors,
        "missing_regions": missing,
        "median_residual_ms": None,
        "regions": completed_regions,
        "recovery_kind": "visual",
    }


def _run_visual_recovery(reference: Path, external: Path, output: Path, analysis,
                         on_progress=None, on_log=None) -> dict:
    if _probe_video_duration(reference) is None or _probe_video_duration(external) is None:
        raise RuntimeError("Görsel Recovery için iki kaynakta da video akışı bulunamadı.")
    if on_log:
        on_log("Ses eşleşmesi düşük güven verdi; iki video üzerinden Visual Cut Recovery başlatılıyor.")
    if on_progress:
        on_progress(42)
    plan = _visual_plan(reference, external, analysis)
    if not plan.get("usable"):
        raise RuntimeError(
            "Visual Cut Recovery yeterli görsel eşleşme çıkaramadı "
            f"(anchor {int(plan.get('anchors') or 0)}, kapsama %{float(plan.get('coverage') or 0) * 100:.1f}, "
            f"güven {float(plan.get('confidence') or 0):.2f}, neden {plan.get('reason')})."
        )
    if on_log:
        on_log(
            f"Visual plan hazır: {int(plan.get('anchors') or 0)} anchor, "
            f"{len(plan.get('regions') or [])} bölge, kapsama %{float(plan.get('coverage') or 0) * 100:.1f}."
        )
    if on_progress:
        on_progress(56)
    result = _render_hybrid(reference, external, output, plan, on_progress, on_log)
    result["recovery_kind"] = "visual"
    return result


def _load_pcm(path: Path):
    """Load analysis PCM without holding an OS-level file mapping open.

    NumPy memmap keeps the backing file handle/mapping alive for the ndarray
    lifetime. On Windows that prevents TemporaryDirectory from deleting
    output.s16le/reference.s16le and caused WinError 32 immediately after a
    successful Hybrid/Visual verification pass. HYBRID_PCM_RATE is only 8 kHz,
    so loading the mono int16 analysis PCM into RAM is bounded and removes the
    Windows file-lock lifetime hazard.
    """
    import numpy as np
    return np.fromfile(path, dtype=np.int16)


def _spectral_features(samples, rate: int):
    """Compact language-tolerant fingerprint: multiband energy + dynamics at 5 Hz."""
    import numpy as np

    frame_samples = max(256, int(round(rate / HYBRID_FEATURE_RATE)))
    count = int(len(samples) // frame_samples)
    if count < 20:
        return np.empty((0, 8), dtype=np.float32)

    nfft = 1
    while nfft < frame_samples:
        nfft <<= 1
    freqs = np.fft.rfftfreq(nfft, 1.0 / rate)
    bands = [(60, 180), (180, 400), (400, 800), (800, 1600), (1600, 3000), (3000, min(3900, rate / 2 - 1))]
    band_masks = [(freqs >= low) & (freqs < high) for low, high in bands]
    window = np.hanning(frame_samples).astype(np.float32)
    output = np.zeros((count, 8), dtype=np.float32)

    chunk_frames = 192
    for first in range(0, count, chunk_frames):
        n = min(chunk_frames, count - first)
        raw = np.asarray(
            samples[first * frame_samples:(first + n) * frame_samples], dtype=np.float32
        ).reshape(n, frame_samples) / 32768.0
        rms = np.sqrt(np.mean(raw * raw, axis=1) + 1e-10)
        dynamics = np.mean(np.abs(np.diff(raw, axis=1)), axis=1)
        spectrum = np.abs(np.fft.rfft(raw * window[None, :], n=nfft, axis=1)) ** 2
        for idx, mask in enumerate(band_masks):
            if np.any(mask):
                output[first:first+n, idx] = np.log1p(np.mean(spectrum[:, mask], axis=1) * 30.0)
        output[first:first+n, 6] = np.log1p(rms * 40.0)
        output[first:first+n, 7] = np.log1p(dynamics * 80.0)

    median = np.median(output, axis=0)
    mad = np.median(np.abs(output - median), axis=0)
    scale = np.maximum(mad * 1.4826, 0.08)
    output = (output - median) / scale
    return np.clip(output, -5.0, 5.0).astype(np.float32)


def _match_anchor(ref_features, ext_features, ext_center_sec: float, expected_ref_sec: float,
                  radius_sec: float, window_sec: float = 16.0):
    import numpy as np
    from numpy.lib.stride_tricks import sliding_window_view

    rate = HYBRID_FEATURE_RATE
    half = max(12, int(round(window_sec * rate / 2.0)))
    ext_center = int(round(ext_center_sec * rate))
    ext_start = ext_center - half
    ext_end = ext_center + half
    if ext_start < 0 or ext_end > len(ext_features):
        return None
    anchor = np.asarray(ext_features[ext_start:ext_end], dtype=np.float32)
    length = len(anchor)
    if length < 20:
        return None

    expected_center = int(round(expected_ref_sec * rate))
    radius = int(round(radius_sec * rate))
    candidate_start = max(0, expected_center - radius - length // 2)
    candidate_end = min(len(ref_features), expected_center + radius + length // 2)
    block = np.asarray(ref_features[candidate_start:candidate_end], dtype=np.float32)
    if len(block) < length + 2:
        return None

    windows = sliding_window_view(block, length, axis=0)
    # numpy places the sliding dimension last for a 2D array: [candidates, features, time]
    if windows.ndim == 3 and windows.shape[1] == ref_features.shape[1]:
        windows = np.swapaxes(windows, 1, 2)
    candidates = windows.reshape(windows.shape[0], -1)
    anchor_flat = anchor.reshape(-1)
    anchor_norm = float(np.linalg.norm(anchor_flat)) + 1e-8
    candidate_norm = np.linalg.norm(candidates, axis=1) + 1e-8
    scores = (candidates @ anchor_flat) / (candidate_norm * anchor_norm)
    best_index = int(np.argmax(scores))
    best_score = float(scores[best_index])
    if len(scores) > 8:
        masked = scores.copy()
        lo = max(0, best_index - 3)
        hi = min(len(masked), best_index + 4)
        masked[lo:hi] = -2.0
        second = float(np.max(masked))
    else:
        second = -1.0
    margin = best_score - second
    ref_start = candidate_start + best_index
    ref_center_sec = (ref_start + length / 2.0) / rate
    return ref_center_sec, best_score, margin


def _envelope(samples, rate: int, start_sec: float, duration_sec: float, block_sec: float = 0.02):
    import numpy as np
    start = max(0, int(round(start_sec * rate)))
    end = min(len(samples), int(round((start_sec + duration_sec) * rate)))
    block = max(32, int(round(block_sec * rate)))
    length = max(0, end - start)
    count = length // block
    if count < 30:
        return np.empty(0, dtype=np.float32)
    raw = np.asarray(samples[start:start + count * block], dtype=np.float32).reshape(count, block)
    env = np.mean(np.abs(raw), axis=1)
    env = np.log1p(env)
    env -= np.mean(env)
    std = float(np.std(env))
    if std > 1e-6:
        env /= std
    return env.astype(np.float32)


def _fine_refine(ref_samples, ext_samples, rate: int, ext_center: float, ref_center: float):
    """20 ms timing refinement around a coarse cross-language match."""
    import numpy as np
    from numpy.lib.stride_tricks import sliding_window_view

    window = 8.0
    radius = 1.2
    ext_env = _envelope(ext_samples, rate, ext_center - window / 2, window)
    ref_env = _envelope(ref_samples, rate, ref_center - window / 2 - radius, window + radius * 2)
    if len(ext_env) < 40 or len(ref_env) <= len(ext_env):
        return ref_center, 0.0
    windows = sliding_window_view(ref_env, len(ext_env))
    norms = np.linalg.norm(windows, axis=1) * (float(np.linalg.norm(ext_env)) + 1e-8) + 1e-8
    scores = (windows @ ext_env) / norms
    idx = int(np.argmax(scores))
    block_sec = 0.02
    shift = idx * block_sec - radius
    return ref_center + shift, float(scores[idx])


def _robust_fit(pairs: list[tuple[float, float, float]]):
    import numpy as np
    if len(pairs) < 2:
        return None
    arr = np.asarray(pairs, dtype=np.float64)
    x = arr[:, 0]
    y = arr[:, 1]
    score = arr[:, 2]
    mask = np.ones(len(arr), dtype=bool)
    for _ in range(4):
        if np.count_nonzero(mask) < 2:
            return None
        rate, intercept = np.polyfit(x[mask], y[mask], 1)
        residual = y - (rate * x + intercept)
        active = np.abs(residual[mask])
        med = float(np.median(active)) if len(active) else 99.0
        mad = float(np.median(np.abs(active - med))) if len(active) else 99.0
        limit = max(0.16, med + 3.0 * max(mad, 0.03))
        new_mask = np.abs(residual) <= min(1.2, limit)
        if np.array_equal(new_mask, mask):
            break
        mask = new_mask
    if np.count_nonzero(mask) < 2:
        return None
    rate, intercept = np.polyfit(x[mask], y[mask], 1)
    if not (0.85 <= rate <= 1.15):
        return None
    residual = y[mask] - (rate * x[mask] + intercept)
    median_residual = float(np.median(np.abs(residual))) if len(residual) else 99.0
    mean_score = float(np.mean(np.clip(score[mask], 0.0, 1.0)))
    count_factor = min(1.0, np.count_nonzero(mask) / 4.0)
    confidence = mean_score * count_factor * math.exp(-median_residual / 0.30)
    return {
        "rate": float(rate),
        "intercept": float(intercept),
        "confidence": float(max(0.0, min(1.0, confidence))),
        "median_residual_sec": median_residual,
        "anchors": int(np.count_nonzero(mask)),
        "pairs": [(float(a), float(b), float(c)) for a, b, c in arr[mask]],
    }


def _recursive_segments(pairs: list[tuple[float, float, float]], depth: int = 0):
    """Fallback segmentation when AST does not provide edit regions."""
    import numpy as np
    if len(pairs) < 3 or depth >= 6:
        fit = _robust_fit(pairs)
        return [(pairs, fit)] if fit else []
    fit = _robust_fit(pairs)
    if fit is None:
        mid = len(pairs) // 2
        return _recursive_segments(pairs[:mid], depth + 1) + _recursive_segments(pairs[mid:], depth + 1)
    x = np.asarray([p[0] for p in pairs], dtype=np.float64)
    y = np.asarray([p[1] for p in pairs], dtype=np.float64)
    residual = np.abs(y - (fit["rate"] * x + fit["intercept"]))
    worst = int(np.argmax(residual))
    if float(residual[worst]) <= 0.9 or worst < 2 or worst > len(pairs) - 3:
        return [(pairs, fit)]
    return _recursive_segments(pairs[:worst], depth + 1) + _recursive_segments(pairs[worst:], depth + 1)


def _hybrid_plan(reference_pcm: Path, external_pcm: Path, rate: int, analysis) -> dict:
    import numpy as np

    ref_samples = _load_pcm(reference_pcm)
    ext_samples = _load_pcm(external_pcm)
    ref_duration = len(ref_samples) / float(rate)
    ext_duration = len(ext_samples) / float(rate)
    ref_features = _spectral_features(ref_samples, rate)
    ext_features = _spectral_features(ext_samples, rate)
    if len(ref_features) < 100 or len(ext_features) < 100:
        return {"usable": False, "reason": "feature_too_short", "regions": []}

    base_delay = float(getattr(analysis, "delay_ms", 0.0) or 0.0) / 1000.0 if analysis is not None else 0.0
    ast_regions = list(getattr(analysis, "offset_regions", ()) or ()) if analysis is not None else []
    plan_regions: list[dict] = []
    all_residuals: list[float] = []
    total_anchors = 0

    if ast_regions:
        for region_index, region in enumerate(ast_regions):
            target_start = max(0.0, float(getattr(region, "start_sec", 0.0)))
            target_end = float(getattr(region, "end_sec", ref_duration))
            if not math.isfinite(target_end) or target_end <= target_start:
                target_end = ref_duration
            target_end = min(ref_duration, target_end)
            lag = float(getattr(region, "lag_ms", base_delay * 1000.0) or 0.0) / 1000.0
            span = target_end - target_start
            if span < 2.0:
                continue

            inset = min(12.0, max(2.0, span * 0.12))
            first = target_start + inset
            last = target_end - inset
            targets: list[float] = []
            if last <= first:
                targets = [(target_start + target_end) / 2.0]
            else:
                step = 30.0
                cursor = first
                while cursor <= last + 0.01:
                    targets.append(cursor)
                    cursor += step
                if targets and last - targets[-1] > 12.0:
                    targets.append(last)

            pairs: list[tuple[float, float, float]] = []
            for target_time in targets:
                ext_center = target_time - lag
                if ext_center < 10.0 or ext_center > ext_duration - 10.0:
                    continue
                match = _match_anchor(ref_features, ext_features, ext_center, target_time, 75.0)
                if match is None:
                    continue
                ref_center, score, margin = match
                if score < 0.10 or margin < -0.01:
                    continue
                refined, fine_score = _fine_refine(ref_samples, ext_samples, rate, ext_center, ref_center)
                combined = max(0.0, min(1.0, (score * 0.78) + (max(0.0, fine_score) * 0.22)))
                pairs.append((ext_center, refined, combined))

            fit = _robust_fit(pairs)
            total_anchors += 0 if fit is None else int(fit["anchors"])
            if fit is None or fit["confidence"] < 0.075:
                plan_regions.append({
                    "target_start_sec": target_start,
                    "target_end_sec": target_end,
                    "missing_source": True,
                    "confidence": 0.0,
                    "rate": 1.0,
                    "source_start_sec": 0.0,
                    "source_end_sec": 0.0,
                    "anchor_count": 0,
                    "residual_ms": None,
                })
                continue

            raw_source_start = (target_start - fit["intercept"]) / fit["rate"]
            raw_source_end = (target_end - fit["intercept"]) / fit["rate"]
            matched_target_start = max(target_start, fit["intercept"])
            matched_target_end = min(target_end, fit["rate"] * ext_duration + fit["intercept"])
            source_start = max(0.0, raw_source_start)
            source_end = min(ext_duration, raw_source_end)

            # A positive offset can mean the external dub starts after the target
            # video. Preserve that unmatched intro (and the equivalent tail case)
            # as original reference audio instead of stretching source t=0 over it.
            if matched_target_start > target_start + 0.08:
                plan_regions.append({
                    "target_start_sec": target_start,
                    "target_end_sec": matched_target_start,
                    "missing_source": True,
                    "confidence": 0.0,
                    "rate": 1.0,
                    "source_start_sec": 0.0,
                    "source_end_sec": 0.0,
                    "anchor_count": 0,
                    "residual_ms": None,
                })

            matched_span = max(0.0, matched_target_end - matched_target_start)
            if source_end - source_start < max(1.0, matched_span * 0.35) or matched_span < 0.25:
                plan_regions.append({
                    "target_start_sec": matched_target_start,
                    "target_end_sec": matched_target_end,
                    "missing_source": True,
                    "confidence": 0.0,
                    "rate": 1.0,
                    "source_start_sec": 0.0,
                    "source_end_sec": 0.0,
                    "anchor_count": 0,
                    "residual_ms": None,
                })
            else:
                residual_ms = float(fit["median_residual_sec"] * 1000.0)
                all_residuals.append(residual_ms)
                plan_regions.append({
                    "target_start_sec": matched_target_start,
                    "target_end_sec": matched_target_end,
                    "source_start_sec": source_start,
                    "source_end_sec": source_end,
                    "rate": float(fit["rate"]),
                    "confidence": float(fit["confidence"]),
                    "missing_source": False,
                    "anchor_count": int(fit["anchors"]),
                    "residual_ms": residual_ms,
                })

            if matched_target_end < target_end - 0.08:
                plan_regions.append({
                    "target_start_sec": matched_target_end,
                    "target_end_sec": target_end,
                    "missing_source": True,
                    "confidence": 0.0,
                    "rate": 1.0,
                    "source_start_sec": 0.0,
                    "source_end_sec": 0.0,
                    "anchor_count": 0,
                    "residual_ms": None,
                })
    else:
        pairs: list[tuple[float, float, float]] = []
        ext_time = 25.0
        while ext_time < ext_duration - 25.0:
            expected = ext_time + base_delay
            match = _match_anchor(ref_features, ext_features, ext_time, expected, 120.0)
            if match is not None:
                ref_center, score, margin = match
                if score >= 0.11 and margin >= -0.01:
                    refined, fine_score = _fine_refine(ref_samples, ext_samples, rate, ext_time, ref_center)
                    combined = max(0.0, min(1.0, score * 0.8 + max(0.0, fine_score) * 0.2))
                    pairs.append((ext_time, refined, combined))
            ext_time += 45.0
        pairs.sort(key=lambda item: item[0])
        segments = _recursive_segments(pairs)
        for index, (segment_pairs, fit) in enumerate(segments):
            if not fit or fit["confidence"] < 0.08:
                continue
            x0 = segment_pairs[0][0]
            x1 = segment_pairs[-1][0]
            if index == 0:
                source_start = 0.0
            else:
                source_start = max(0.0, (segments[index - 1][0][-1][0] + x0) / 2.0)
            if index == len(segments) - 1:
                source_end = ext_duration
            else:
                source_end = min(ext_duration, (x1 + segments[index + 1][0][0][0]) / 2.0)
            target_start = max(0.0, fit["rate"] * source_start + fit["intercept"])
            target_end = min(ref_duration, fit["rate"] * source_end + fit["intercept"])
            if target_end <= target_start:
                continue
            residual_ms = float(fit["median_residual_sec"] * 1000.0)
            all_residuals.append(residual_ms)
            total_anchors += int(fit["anchors"])
            plan_regions.append({
                "target_start_sec": target_start,
                "target_end_sec": target_end,
                "source_start_sec": source_start,
                "source_end_sec": source_end,
                "rate": float(fit["rate"]),
                "confidence": float(fit["confidence"]),
                "missing_source": False,
                "anchor_count": int(fit["anchors"]),
                "residual_ms": residual_ms,
            })

    plan_regions.sort(key=lambda item: item["target_start_sec"])
    matched_duration = sum(
        max(0.0, item["target_end_sec"] - item["target_start_sec"])
        for item in plan_regions if not item["missing_source"]
    )
    coverage = min(1.0, matched_duration / max(ref_duration, 1.0))
    matched = [item for item in plan_regions if not item["missing_source"]]
    weighted_conf = 0.0
    if matched:
        denom = sum(max(0.1, item["target_end_sec"] - item["target_start_sec"]) for item in matched)
        weighted_conf = sum(
            item["confidence"] * max(0.1, item["target_end_sec"] - item["target_start_sec"])
            for item in matched
        ) / max(denom, 0.1)
    confidence = weighted_conf * min(1.0, 0.35 + coverage)
    usable = bool(matched) and coverage >= 0.55 and confidence >= 0.055 and total_anchors >= 2
    median_residual = float(np.median(all_residuals)) if all_residuals else None
    return {
        "usable": usable,
        "reference_duration_sec": ref_duration,
        "external_duration_sec": ext_duration,
        "coverage": coverage,
        "confidence": confidence,
        "anchors": total_anchors,
        "missing_regions": sum(1 for item in plan_regions if item["missing_source"]),
        "median_residual_ms": median_residual,
        "regions": plan_regions,
    }



def _analysis_expected_target(source_sec: float, analysis, ref_duration: float,
                              ext_duration: float) -> float:
    """Predict target time from AST hints without making visual input mandatory."""
    ratio = ref_duration / max(ext_duration, 1.0)
    delay = float(getattr(analysis, "delay_ms", 0.0) or 0.0) / 1000.0 if analysis is not None else 0.0
    predicted = source_sec * ratio + delay
    if analysis is None:
        return predicted
    for region in (getattr(analysis, "offset_regions", ()) or ()):
        target_start = float(getattr(region, "start_sec", 0.0) or 0.0)
        target_end = float(getattr(region, "end_sec", ref_duration) or ref_duration)
        if not math.isfinite(target_end):
            target_end = ref_duration
        lag = float(getattr(region, "lag_ms", delay * 1000.0) or 0.0) / 1000.0
        source_start = target_start - lag
        source_end = target_end - lag
        if source_start - 30.0 <= source_sec <= source_end + 30.0:
            return source_sec + lag
    return predicted


def _monotonic_audio_path(pairs: list[tuple[float, float, float]]):
    """Select a globally monotonic chain while allowing hard edit jumps."""
    if not pairs:
        return []
    pairs = sorted(pairs, key=lambda item: (item[0], item[1]))
    n = len(pairs)
    scores = [0.0] * n
    parent = [-1] * n
    for i, (source, target, quality) in enumerate(pairs):
        scores[i] = 0.5 + max(0.0, quality)
        for j in range(i):
            prev_source, prev_target, _ = pairs[j]
            ds = source - prev_source
            dt = target - prev_target
            if ds <= 0.0 or dt <= 0.0:
                continue
            # PAL/film speed and normal encode drift sit close to 1.0. Hard edits
            # are allowed as positive target jumps, but time reversal is forbidden.
            if dt < ds * 0.72 - 12.0:
                continue
            if dt > ds * 1.38 + 240.0:
                continue
            continuity = math.exp(-abs((dt / max(ds, 1e-6)) - 1.0) * 1.3)
            candidate = scores[j] + 0.5 + max(0.0, quality) + continuity * 0.25
            if candidate > scores[i]:
                scores[i] = candidate
                parent[i] = j
    index = max(range(n), key=lambda idx: scores[idx])
    chain = []
    while index >= 0:
        chain.append(pairs[index])
        index = parent[index]
    chain.reverse()
    return chain


def _banded_dtw_refine(ref_features, ext_features, source_start: float, source_end: float,
                       rate: float, intercept: float, band_sec: float = 10.0):
    """Refine a linear segment with a narrow, monotonic DTW corridor.

    Features are downsampled from 5 Hz to 1 Hz. The corridor is centred on the
    coarse source->target line, so complexity is O(duration * band) rather than
    O(duration^2). This keeps feature-film analysis practical.
    """
    import numpy as np

    feature_rate = HYBRID_FEATURE_RATE
    stride = max(1, int(round(feature_rate)))
    source_i0 = max(0, int(round(source_start * feature_rate)))
    source_i1 = min(len(ext_features), int(round(source_end * feature_rate)))
    if source_i1 - source_i0 < stride * 18:
        return None

    ext = np.asarray(ext_features[source_i0:source_i1:stride], dtype=np.float32)
    if len(ext) < 18:
        return None

    target_start = max(0.0, rate * source_start + intercept - band_sec - 2.0)
    target_end = min(
        len(ref_features) / feature_rate,
        rate * source_end + intercept + band_sec + 2.0,
    )
    target_i0 = max(0, int(round(target_start * feature_rate)))
    target_i1 = min(len(ref_features), int(round(target_end * feature_rate)))
    ref = np.asarray(ref_features[target_i0:target_i1:stride], dtype=np.float32)
    if len(ref) < 18:
        return None

    ext_norm = ext / np.maximum(np.linalg.norm(ext, axis=1, keepdims=True), 1e-6)
    ref_norm = ref / np.maximum(np.linalg.norm(ref, axis=1, keepdims=True), 1e-6)

    source_times = source_start + np.arange(len(ext), dtype=np.float64)
    target_base = target_i0 / feature_rate
    band = max(5, int(math.ceil(band_sec)))
    prev: dict[int, float] = {}
    parents: dict[tuple[int, int], tuple[int, int] | None] = {}

    for i, source_time in enumerate(source_times):
        expected_time = rate * float(source_time) + intercept
        expected_j = int(round(expected_time - target_base))
        lo = max(0, expected_j - band)
        hi = min(len(ref) - 1, expected_j + band)
        if lo > hi:
            return None
        curr: dict[int, float] = {}
        for j in range(lo, hi + 1):
            similarity = float(ref_norm[j] @ ext_norm[i])
            local_cost = max(0.0, 1.0 - similarity)
            best_cost = math.inf
            best_parent = None

            if i == 0:
                # Small preference for the centre of the expected corridor.
                best_cost = local_cost + abs(j - expected_j) * 0.015
            else:
                for pj, penalty in ((j - 1, 0.0), (j, 0.08), (j - 2, 0.05)):
                    if pj in prev:
                        candidate = prev[pj] + local_cost + penalty
                        if candidate < best_cost:
                            best_cost = candidate
                            best_parent = (i - 1, pj)

            # Allow target-only advancement (inserted target material) while
            # retaining monotonicity.
            if j - 1 in curr:
                candidate = curr[j - 1] + local_cost + 0.10
                if candidate < best_cost:
                    best_cost = candidate
                    best_parent = (i, j - 1)

            if math.isfinite(best_cost):
                curr[j] = best_cost
                parents[(i, j)] = best_parent
        if not curr:
            return None
        prev = curr

    end_j = min(prev, key=prev.get)
    state = (len(ext) - 1, end_j)
    path = []
    guard = 0
    while state is not None and guard < (len(ext) + len(ref)) * 3:
        i, j = state
        path.append((i, j))
        state = parents.get(state)
        guard += 1
    path.reverse()
    if len(path) < max(12, len(ext) // 4):
        return None

    # Collapse horizontal target-only steps so each source second contributes one
    # timeline point. The median target position is robust to short insertions.
    grouped: dict[int, list[int]] = {}
    for i, j in path:
        grouped.setdefault(i, []).append(j)
    pairs = []
    for i in sorted(grouped):
        js = grouped[i]
        source_time = source_start + float(i)
        target_time = target_base + float(np.median(js))
        similarity = float(ref_norm[int(round(np.median(js)))] @ ext_norm[i])
        pairs.append((source_time, target_time, max(0.0, min(1.0, (similarity + 1.0) / 2.0))))

    fit = _robust_fit(pairs)
    if fit is None or fit["anchors"] < 8:
        return None
    mean_cost = float(prev[end_j] / max(len(path), 1))
    if mean_cost > 0.95:
        return None
    fit["dtw_cost"] = mean_cost
    return fit


def _complete_timeline_regions(regions: list[dict], ref_duration: float) -> tuple[list[dict], int]:
    """Sort matched regions and explicitly fill target gaps with original audio."""
    completed: list[dict] = []
    cursor = 0.0
    missing = 0
    for region in sorted(regions, key=lambda item: float(item["target_start_sec"])):
        start = max(cursor, max(0.0, float(region["target_start_sec"])))
        end = min(ref_duration, float(region["target_end_sec"]))
        if start > cursor + 0.35:
            completed.append({
                "target_start_sec": cursor,
                "target_end_sec": start,
                "source_start_sec": 0.0,
                "source_end_sec": 0.0,
                "rate": 1.0,
                "confidence": 0.0,
                "missing_source": True,
                "anchor_count": 0,
                "residual_ms": None,
            })
            missing += 1
        if end <= start + 0.20:
            continue
        adjusted = dict(region)
        original_start = float(region["target_start_sec"])
        if start > original_start and not adjusted.get("missing_source"):
            local_rate = max(float(adjusted.get("rate") or 1.0), 1e-6)
            adjusted["source_start_sec"] = float(adjusted.get("source_start_sec") or 0.0) + (start - original_start) / local_rate
        adjusted["target_start_sec"] = start
        adjusted["target_end_sec"] = end
        completed.append(adjusted)
        cursor = max(cursor, end)
    if cursor < ref_duration - 0.35:
        completed.append({
            "target_start_sec": cursor,
            "target_end_sec": ref_duration,
            "source_start_sec": 0.0,
            "source_end_sec": 0.0,
            "rate": 1.0,
            "confidence": 0.0,
            "missing_source": True,
            "anchor_count": 0,
            "residual_ms": None,
        })
        missing += 1
    return completed, missing


def _audio_timeline_plan(reference_pcm: Path, external_pcm: Path, rate: int, analysis) -> dict:
    """Audio-first timeline mapper for raw dub files with no video stream."""
    import numpy as np

    ref_samples = _load_pcm(reference_pcm)
    ext_samples = _load_pcm(external_pcm)
    ref_duration = len(ref_samples) / float(rate)
    ext_duration = len(ext_samples) / float(rate)
    ref_features = _spectral_features(ref_samples, rate)
    ext_features = _spectral_features(ext_samples, rate)
    if len(ref_features) < 100 or len(ext_features) < 100:
        return {"usable": False, "reason": "audio_feature_too_short", "regions": []}

    candidates: list[tuple[float, float, float]] = []
    source_time = 22.0
    while source_time < ext_duration - 22.0:
        expected = _analysis_expected_target(source_time, analysis, ref_duration, ext_duration)
        radius = 180.0 if analysis is not None else 300.0
        match = _match_anchor(ref_features, ext_features, source_time, expected, radius, window_sec=20.0)
        if match is not None:
            ref_center, score, margin = match
            if score >= 0.095 and margin >= -0.018:
                refined, fine_score = _fine_refine(
                    ref_samples, ext_samples, rate, source_time, ref_center
                )
                fine_quality = max(0.0, min(1.0, (fine_score + 0.20) / 1.20))
                quality = max(0.0, min(1.0, score * 0.78 + fine_quality * 0.22))
                candidates.append((source_time, refined, quality))
        source_time += 24.0

    chain = _monotonic_audio_path(candidates)
    if len(chain) < 8:
        return {
            "usable": False,
            "reason": "audio_anchor_floor",
            "regions": [],
            "anchors": len(chain),
            "coverage": 0.0,
            "confidence": 0.0,
            "dtw_regions": 0,
        }

    segments = _recursive_segments(chain)
    good = []
    for pairs, fit in segments:
        if fit is None or fit["anchors"] < 2 or fit["confidence"] < 0.045:
            continue
        good.append((pairs, fit))
    if not good:
        return {
            "usable": False,
            "reason": "audio_fit_floor",
            "regions": [],
            "anchors": len(chain),
            "coverage": 0.0,
            "confidence": 0.0,
            "dtw_regions": 0,
        }

    regions: list[dict] = []
    total_anchors = 0
    dtw_regions = 0
    confidence_values = []
    for index, (pairs, fit) in enumerate(good):
        first_source = float(pairs[0][0])
        last_source = float(pairs[-1][0])
        if index == 0:
            source_start = max(0.0, first_source - 14.0)
        else:
            source_start = max(0.0, (float(good[index - 1][0][-1][0]) + first_source) / 2.0)
        if index == len(good) - 1:
            source_end = min(ext_duration, last_source + 14.0)
        else:
            source_end = min(ext_duration, (last_source + float(good[index + 1][0][0][0])) / 2.0)

        local_fit = fit
        if source_end - source_start >= 35.0:
            dtw_fit = _banded_dtw_refine(
                ref_features,
                ext_features,
                source_start,
                source_end,
                float(fit["rate"]),
                float(fit["intercept"]),
                band_sec=12.0,
            )
            if (
                dtw_fit is not None and
                abs(float(dtw_fit["rate"]) - float(fit["rate"])) <= 0.025 and
                float(dtw_fit["median_residual_sec"]) <= 0.65
            ):
                # Blend coarse edit-stable anchors with the dense DTW fit.
                blend = 0.65
                local_fit = dict(fit)
                local_fit["rate"] = float(fit["rate"]) * (1.0 - blend) + float(dtw_fit["rate"]) * blend
                local_fit["intercept"] = float(fit["intercept"]) * (1.0 - blend) + float(dtw_fit["intercept"]) * blend
                local_fit["confidence"] = max(float(fit["confidence"]), min(1.0, float(dtw_fit["confidence"]) * 0.9))
                local_fit["median_residual_sec"] = min(
                    float(fit["median_residual_sec"]),
                    float(dtw_fit["median_residual_sec"]),
                )
                dtw_regions += 1

        target_start = max(0.0, float(local_fit["rate"]) * source_start + float(local_fit["intercept"]))
        target_end = min(ref_duration, float(local_fit["rate"]) * source_end + float(local_fit["intercept"]))
        if target_end - target_start < 5.0:
            continue
        regions.append({
            "target_start_sec": target_start,
            "target_end_sec": target_end,
            "source_start_sec": source_start,
            "source_end_sec": source_end,
            "rate": float(local_fit["rate"]),
            "confidence": float(local_fit["confidence"]),
            "missing_source": False,
            "anchor_count": int(fit["anchors"]),
            "residual_ms": float(local_fit["median_residual_sec"] * 1000.0),
        })
        total_anchors += int(fit["anchors"])
        confidence_values.append(float(local_fit["confidence"]))

    completed, missing = _complete_timeline_regions(regions, ref_duration)
    matched_duration = sum(
        max(0.0, float(r["target_end_sec"]) - float(r["target_start_sec"]))
        for r in completed if not r.get("missing_source")
    )
    coverage = min(1.0, matched_duration / max(ref_duration, 1.0))
    confidence = float(np.mean(confidence_values)) if confidence_values else 0.0
    source_span = chain[-1][0] - chain[0][0] if len(chain) >= 2 else 0.0
    span_ratio = source_span / max(ext_duration, 1.0)
    usable = (
        total_anchors >= 8 and
        coverage >= 0.50 and
        confidence >= 0.045 and
        span_ratio >= 0.35
    )
    return {
        "usable": bool(usable),
        "reason": "audio_timeline_ok" if usable else "audio_timeline_quality_floor",
        "reference_duration_sec": ref_duration,
        "external_duration_sec": ext_duration,
        "coverage": coverage,
        "confidence": confidence,
        "anchors": total_anchors,
        "waveform_anchors": total_anchors,
        "visual_anchors": 0,
        "dtw_regions": dtw_regions,
        "missing_regions": missing,
        "regions": completed,
        "recovery_kind": "audio_timeline",
    }


def _audio_refine_visual_plan(reference_pcm: Path, external_pcm: Path, rate: int,
                              visual_plan: dict) -> dict:
    """Micro-align a strong visual edit map without allowing weak dub matches to damage it.

    Rev10.8 accepted a single very low-confidence cross-language waveform match and
    could shift a visually strong region by as much as 1.8 seconds. In the reported
    sample this worsened a 332-anchor visual map. Rev10.8.1 therefore treats the
    visual map as authoritative and only accepts waveform corrections when multiple
    independent probes agree, or one probe is exceptionally strong.
    """
    import numpy as np

    ref_samples = _load_pcm(reference_pcm)
    ext_samples = _load_pcm(external_pcm)
    ref_features = _spectral_features(ref_samples, rate)
    ext_features = _spectral_features(ext_samples, rate)
    ext_duration = len(ext_samples) / float(rate)

    refined_regions = []
    waveform_anchors = 0
    quality_values = []
    dtw_regions = 0

    for region in visual_plan.get("regions") or []:
        if region.get("missing_source"):
            refined_regions.append(dict(region))
            continue

        current = dict(region)
        target_start = float(current["target_start_sec"])
        target_end = float(current["target_end_sec"])
        source_start = float(current["source_start_sec"])
        source_end = float(current["source_end_sec"])
        local_rate = max(float(current.get("rate") or 1.0), 1e-6)
        span = target_end - target_start

        fractions = [0.5]
        if span >= 75.0:
            fractions = [0.25, 0.50, 0.75]

        accepted = []
        for fraction in fractions:
            target = target_start + span * fraction
            source = source_start + (source_end - source_start) * fraction
            if source < 8.0 or source > ext_duration - 8.0:
                continue

            match = _match_anchor(
                ref_features, ext_features, source, target, 3.0, window_sec=14.0
            )
            if match is None:
                continue

            ref_center, score, margin = match
            # Cross-language audio is noisy. Do not let a broad/ambiguous peak
            # move a visually reliable timeline.
            if score < 0.14 or margin < 0.010:
                continue

            refined, fine_score = _fine_refine(
                ref_samples, ext_samples, rate, source, ref_center
            )
            if fine_score < 0.02:
                continue

            delta = refined - target
            if abs(delta) > 0.90:
                continue

            accepted.append((delta, score, margin, fine_score))

        apply_shift = False
        shift_delta = 0.0
        if len(accepted) >= 2:
            deltas = np.asarray([item[0] for item in accepted], dtype=np.float64)
            median = float(np.median(deltas))
            mad = float(np.median(np.abs(deltas - median)))
            spread = float(np.max(deltas) - np.min(deltas))
            if mad <= 0.20 and spread <= 0.65 and abs(median) <= 0.90:
                apply_shift = True
                shift_delta = median
        elif len(accepted) == 1:
            delta, score, margin, fine_score = accepted[0]
            # One probe is only allowed to move the visual plan if it is unusually
            # distinctive and the requested correction is small.
            if score >= 0.30 and margin >= 0.030 and fine_score >= 0.20 and abs(delta) <= 0.45:
                apply_shift = True
                shift_delta = float(delta)

        if apply_shift:
            source_shift = -shift_delta / local_rate
            current["source_start_sec"] = max(0.0, source_start + source_shift)
            current["source_end_sec"] = max(
                float(current["source_start_sec"]) + 0.05,
                source_end + source_shift,
            )
            current["audio_refine_ms"] = shift_delta * 1000.0

            accepted_scores = [max(0.0, min(1.0, item[1])) for item in accepted]
            current["confidence"] = min(
                1.0,
                float(current.get("confidence") or 0.0) * 0.84 +
                float(np.mean(accepted_scores)) * 0.16,
            )
            waveform_anchors += len(accepted)
            quality_values.extend(accepted_scores)

        # DTW may refine local speed, but only after a region already has at least
        # two mutually-consistent waveform anchors. This prevents speech-language
        # differences from overriding a strong visual rate estimate.
        if apply_shift and len(accepted) >= 2 and span >= 90.0:
            intercept = target_start - local_rate * float(current["source_start_sec"])
            dtw_fit = _banded_dtw_refine(
                ref_features,
                ext_features,
                float(current["source_start_sec"]),
                float(current["source_end_sec"]),
                local_rate,
                intercept,
                band_sec=5.0,
            )
            if (
                dtw_fit is not None and
                abs(float(dtw_fit["rate"]) - local_rate) <= 0.006 and
                float(dtw_fit["median_residual_sec"]) <= 0.28
            ):
                current["rate"] = local_rate * 0.80 + float(dtw_fit["rate"]) * 0.20
                dtw_regions += 1

        refined_regions.append(current)

    result = dict(visual_plan)
    result["regions"] = refined_regions
    result["visual_anchors"] = int(visual_plan.get("anchors") or 0)
    result["waveform_anchors"] = waveform_anchors
    result["anchors"] = int(visual_plan.get("anchors") or 0) + waveform_anchors
    result["dtw_regions"] = dtw_regions

    if quality_values:
        result["confidence"] = min(
            1.0,
            float(visual_plan.get("confidence") or 0.0) * 0.90 +
            float(np.mean(quality_values)) * 0.10,
        )
        result["recovery_kind"] = "multimodal"
    else:
        result["recovery_kind"] = "visual"

    return result

def _run_audio_timeline_recovery(reference: Path, external: Path, output: Path, analysis,
                                 on_progress=None, on_log=None) -> dict:
    if on_log:
        on_log("Waveform Timeline Mapper başlatılıyor; görüntü kaynağı zorunlu değil.")
    if on_progress:
        on_progress(42)

    with tempfile.TemporaryDirectory(prefix="altyazidb-audio-timeline-") as td:
        root = Path(td)
        ref_pcm = root / "reference.s16le"
        ext_pcm = root / "external.s16le"
        _decode_pcm(reference, ref_pcm, HYBRID_PCM_RATE)
        if on_progress:
            on_progress(47)
        _decode_pcm(external, ext_pcm, HYBRID_PCM_RATE)
        if on_progress:
            on_progress(51)
        plan = _audio_timeline_plan(ref_pcm, ext_pcm, HYBRID_PCM_RATE, analysis)

    if not plan.get("usable"):
        raise RuntimeError(
            "Waveform Timeline Mapper yeterli güvenilir zaman haritası çıkaramadı "
            f"(anchor {int(plan.get('anchors') or 0)}, kapsama %{float(plan.get('coverage') or 0) * 100:.1f}, "
            f"güven {float(plan.get('confidence') or 0):.2f}, neden {plan.get('reason')})."
        )
    if on_log:
        on_log(
            f"Waveform timeline hazır: {int(plan.get('anchors') or 0)} anchor, "
            f"{len(plan.get('regions') or [])} bölge, DTW {int(plan.get('dtw_regions') or 0)}, "
            f"kapsama %{float(plan.get('coverage') or 0) * 100:.1f}."
        )
    result = _render_hybrid(reference, external, output, plan, on_progress, on_log)
    result.update({
        "recovery_kind": "audio_timeline",
        "timeline_anchors": int(plan.get("anchors") or 0),
        "visual_anchors": 0,
        "waveform_anchors": int(plan.get("waveform_anchors") or plan.get("anchors") or 0),
        "dtw_regions": int(plan.get("dtw_regions") or 0),
    })
    return result


def _run_multimodal_recovery(reference: Path, external: Path, output: Path, analysis,
                             on_progress=None, on_log=None) -> dict:
    if _probe_video_duration(reference) is None or _probe_video_duration(external) is None:
        raise RuntimeError("Multi-Modal görsel katman için iki kaynakta da video akışı bulunamadı.")
    if on_log:
        on_log("Multi-Modal Timeline Sync başlatılıyor: Visual + Waveform + DTW.")
    if on_progress:
        on_progress(42)

    visual_plan = _visual_plan(reference, external, analysis)
    if not visual_plan.get("usable"):
        raise RuntimeError(
            "Visual Timeline Mapper yeterli kurgu haritası çıkaramadı "
            f"(anchor {int(visual_plan.get('anchors') or 0)}, "
            f"kapsama %{float(visual_plan.get('coverage') or 0) * 100:.1f}, "
            f"neden {visual_plan.get('reason')})."
        )
    if on_log:
        on_log(
            f"Visual timeline: {int(visual_plan.get('anchors') or 0)} anchor, "
            f"{len(visual_plan.get('regions') or [])} bölge, "
            f"kapsama %{float(visual_plan.get('coverage') or 0) * 100:.1f}."
        )
    if on_progress:
        on_progress(50)

    with tempfile.TemporaryDirectory(prefix="altyazidb-multimodal-") as td:
        root = Path(td)
        ref_pcm = root / "reference.s16le"
        ext_pcm = root / "external.s16le"
        _decode_pcm(reference, ref_pcm, HYBRID_PCM_RATE)
        _decode_pcm(external, ext_pcm, HYBRID_PCM_RATE)
        plan = _audio_refine_visual_plan(ref_pcm, ext_pcm, HYBRID_PCM_RATE, visual_plan)

    if on_log:
        on_log(
            f"Multi-Modal timeline hazır: görsel {int(plan.get('visual_anchors') or 0)}, "
            f"waveform {int(plan.get('waveform_anchors') or 0)}, "
            f"DTW {int(plan.get('dtw_regions') or 0)}, "
            f"kapsama %{float(plan.get('coverage') or 0) * 100:.1f}."
        )
    result = _render_hybrid(reference, external, output, plan, on_progress, on_log)
    result.update({
        "recovery_kind": str(plan.get("recovery_kind") or "multimodal"),
        "timeline_anchors": int(plan.get("anchors") or 0),
        "visual_anchors": int(plan.get("visual_anchors") or 0),
        "waveform_anchors": int(plan.get("waveform_anchors") or 0),
        "dtw_regions": int(plan.get("dtw_regions") or 0),
    })
    return result


def _escape_concat(path: Path) -> str:
    return str(path).replace("'", "'\\''")


def _atempo_chain(value: float) -> str:
    value = max(0.25, min(4.0, value))
    factors = []
    while value < 0.5:
        factors.append(0.5)
        value /= 0.5
    while value > 2.0:
        factors.append(2.0)
        value /= 2.0
    factors.append(value)
    return ",".join(f"atempo={factor:.9f}" for factor in factors)


def _verify_hybrid_output_detailed(reference: Path, output: Path, plan: dict) -> dict:
    """Measure signed post-render residuals and retain the region they belong to.

    Positive residual means the rendered dub is early at that probe: the content
    heard at target time T best matches the reference at T+residual. This signed
    value can therefore be fed back into the source extraction point.
    """
    import numpy as np

    with tempfile.TemporaryDirectory(prefix="altyazidb-hybrid-verify-") as td:
        root = Path(td)
        ref_pcm = root / "reference.s16le"
        out_pcm = root / "output.s16le"
        _decode_pcm(reference, ref_pcm, HYBRID_PCM_RATE)
        _decode_pcm(output, out_pcm, HYBRID_PCM_RATE)
        ref_samples = _load_pcm(ref_pcm)
        out_samples = _load_pcm(out_pcm)
        ref_features = _spectral_features(ref_samples, HYBRID_PCM_RATE)
        out_features = _spectral_features(out_samples, HYBRID_PCM_RATE)

        observations = []
        matched_index = -1
        for region in plan.get("regions") or []:
            if region.get("missing_source"):
                continue

            matched_index += 1
            start = float(region.get("target_start_sec") or 0.0)
            end = float(region.get("target_end_sec") or start)
            span = end - start
            if span < 12.0:
                continue

            if span >= 150.0:
                fractions = [0.20, 0.40, 0.60, 0.80]
            elif span >= 70.0:
                fractions = [0.28, 0.50, 0.72]
            elif span >= 35.0:
                fractions = [0.35, 0.65]
            else:
                fractions = [0.50]

            for fraction in fractions:
                target = start + span * fraction
                max_time = min(len(ref_samples), len(out_samples)) / HYBRID_PCM_RATE
                if target < 6.0 or target > max_time - 6.0:
                    continue

                match = _match_anchor(
                    ref_features, out_features, target, target, 1.8, window_sec=10.0
                )
                if match is None:
                    continue

                ref_center, score, margin = match
                if score < 0.10 or margin < -0.01:
                    continue

                refined, fine_score = _fine_refine(
                    ref_samples, out_samples, HYBRID_PCM_RATE, target, ref_center
                )
                if fine_score < -0.10:
                    continue

                residual = float(refined - target)
                if abs(residual) > 1.6:
                    continue

                observations.append({
                    "region_index": matched_index,
                    "target_sec": target,
                    "residual_sec": residual,
                    "score": float(score),
                    "fine_score": float(fine_score),
                })

        if not observations:
            return {
                "p75_ms": None,
                "count": 0,
                "signed_median_ms": None,
                "mad_ms": None,
                "observations": [],
            }

        residuals = np.asarray(
            [item["residual_sec"] for item in observations], dtype=np.float64
        )
        abs_values = np.abs(residuals)
        median = float(np.median(residuals))
        mad = float(np.median(np.abs(residuals - median)))

        return {
            "p75_ms": float(np.percentile(abs_values, 75) * 1000.0),
            "count": len(observations),
            "signed_median_ms": median * 1000.0,
            "mad_ms": mad * 1000.0,
            "observations": observations,
        }


def _verify_hybrid_output(reference: Path, output: Path, plan: dict) -> tuple[float | None, int]:
    detail = _verify_hybrid_output_detailed(reference, output, plan)
    return detail["p75_ms"], int(detail["count"])


def _post_render_calibrated_plan(plan: dict, detail: dict) -> tuple[dict | None, list[float]]:
    """Create one conservative second-pass timeline from measured rendered residuals."""
    import numpy as np

    observations = list(detail.get("observations") or [])
    if len(observations) < 2:
        return None, []

    global_values = np.asarray(
        [float(item["residual_sec"]) for item in observations], dtype=np.float64
    )
    global_median = float(np.median(global_values))
    global_mad = float(np.median(np.abs(global_values - global_median)))

    # A broad or contradictory verification cloud is not safe to feed back.
    if abs(global_median) > 1.20 or global_mad > 0.32:
        return None, []

    grouped = {}
    for item in observations:
        grouped.setdefault(int(item["region_index"]), []).append(float(item["residual_sec"]))

    corrected = dict(plan)
    corrected_regions = []
    matched_index = -1
    corrections_ms = []

    for region in plan.get("regions") or []:
        current = dict(region)
        if region.get("missing_source"):
            corrected_regions.append(current)
            continue

        matched_index += 1
        local_values = grouped.get(matched_index) or []
        correction = None

        if len(local_values) >= 2:
            arr = np.asarray(local_values, dtype=np.float64)
            local_median = float(np.median(arr))
            local_mad = float(np.median(np.abs(arr - local_median)))
            if local_mad <= 0.24 and abs(local_median) <= 1.10:
                correction = local_median
        elif len(local_values) == 1 and len(observations) >= 4:
            # A single local probe may borrow the global correction only when the
            # whole rendered timeline agrees tightly.
            if global_mad <= 0.20:
                correction = global_median

        if correction is None and len(observations) >= 3 and global_mad <= 0.18:
            correction = global_median

        if correction is not None and abs(correction) >= 0.08:
            rate = max(float(current.get("rate") or 1.0), 1e-6)
            source_shift = -correction / rate
            current["source_start_sec"] = max(
                0.0, float(current.get("source_start_sec") or 0.0) + source_shift
            )
            current["source_end_sec"] = max(
                float(current["source_start_sec"]) + 0.05,
                float(current.get("source_end_sec") or 0.0) + source_shift,
            )
            current["post_render_calibration_ms"] = correction * 1000.0
            corrections_ms.append(correction * 1000.0)

        corrected_regions.append(current)

    if not corrections_ms:
        return None, []

    corrected["regions"] = corrected_regions
    corrected["post_render_calibration"] = True
    return corrected, corrections_ms



def _decode_pcm_range(media: Path, output: Path, rate: int, start_sec: float, duration_sec: float) -> None:
    start = max(0.0, float(start_sec))
    duration = max(8.0, float(duration_sec))
    _run_checked([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-ss", f"{start:.6f}", "-t", f"{duration:.6f}", "-i", str(media),
        "-map", "0:a:0", "-vn", "-sn", "-dn",
        "-ac", "1", "-ar", str(rate), "-c:a", "pcm_s16le", "-f", "s16le", str(output),
    ])
    minimum = int(rate * 2 * min(6.0, duration * 0.35))
    if not output.is_file() or output.stat().st_size < minimum:
        raise RuntimeError("Live Progressive için yeterli lokal PCM üretilemedi.")


def _render_live_external_pcm(media: Path, output: Path, rate: int,
                              source_start_sec: float, target_duration_sec: float,
                              mapping_rate: float) -> None:
    mapping_rate = max(0.75, min(1.25, float(mapping_rate)))
    target_duration = max(8.0, float(target_duration_sec))
    source_duration = target_duration / mapping_rate
    tempo = source_duration / target_duration
    _run_checked([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-ss", f"{max(0.0, source_start_sec):.6f}",
        "-t", f"{max(8.0, source_duration):.6f}",
        "-i", str(media), "-map", "0:a:0", "-vn", "-sn", "-dn",
        "-af", _atempo_chain(tempo),
        "-t", f"{target_duration:.6f}",
        "-ac", "1", "-ar", str(rate), "-c:a", "pcm_s16le", "-f", "s16le", str(output),
    ])
    minimum = int(rate * 2 * min(6.0, target_duration * 0.35))
    if not output.is_file() or output.stat().st_size < minimum:
        raise RuntimeError("Live Progressive aday Türkçe PCM üretilemedi.")


def _verify_live_progressive_pair(reference_pcm: Path, external_pcm: Path, rate: int) -> dict:
    import numpy as np

    ref_samples = _load_pcm(reference_pcm)
    ext_samples = _load_pcm(external_pcm)
    common_duration = min(len(ref_samples), len(ext_samples)) / float(rate)
    if common_duration < 14.0:
        return {"p75_ms": None, "count": 0, "signed_median_ms": None, "mad_ms": None,
                "mean_score": 0.0, "observations": []}

    ref_features = _spectral_features(ref_samples, rate)
    ext_features = _spectral_features(ext_samples, rate)
    if common_duration >= 70.0:
        fractions = [0.20, 0.40, 0.60, 0.80]
    elif common_duration >= 42.0:
        fractions = [0.25, 0.50, 0.75]
    else:
        fractions = [0.32, 0.68]

    observations = []
    for fraction in fractions:
        probe = common_duration * fraction
        if probe < 5.0 or probe > common_duration - 5.0:
            continue
        match = _match_anchor(ref_features, ext_features, probe, probe, 2.2, window_sec=9.0)
        if match is None:
            continue
        ref_center, score, margin = match
        if score < 0.08 or margin < 0.003:
            continue
        refined, fine_score = _fine_refine(ref_samples, ext_samples, rate, probe, ref_center)
        if fine_score < -0.05:
            continue
        residual = float(refined - probe)
        if abs(residual) > 2.2:
            continue
        observations.append({
            "target_sec": float(probe),
            "residual_sec": residual,
            "score": float(score),
            "fine_score": float(fine_score),
        })

    if not observations:
        return {"p75_ms": None, "count": 0, "signed_median_ms": None, "mad_ms": None,
                "mean_score": 0.0, "observations": []}

    residuals = np.asarray([item["residual_sec"] for item in observations], dtype=np.float64)
    absolute = np.abs(residuals)
    median = float(np.median(residuals))
    mad = float(np.median(np.abs(residuals - median)))
    mean_score = float(np.mean([max(0.0, min(1.0, item["score"])) for item in observations]))
    return {
        "p75_ms": float(np.percentile(absolute, 75) * 1000.0),
        "count": len(observations),
        "signed_median_ms": median * 1000.0,
        "mad_ms": mad * 1000.0,
        "mean_score": mean_score,
        "observations": observations,
    }


def _live_refine_mapping_from_observations(mapping_rate: float, source_start_sec: float,
                                            target_duration_sec: float,
                                            observations: list[tuple[float, float]]) -> dict | None:
    import numpy as np

    if len(observations) < 2:
        return None
    rate = max(0.75, min(1.25, float(mapping_rate)))
    source_start = max(0.0, float(source_start_sec))
    arr = np.asarray(observations, dtype=np.float64)
    x = arr[:, 0]
    residual = arr[:, 1]
    median = float(np.median(residual))
    mad = float(np.median(np.abs(residual - median)))
    if abs(median) > 2.0 or mad > 0.65:
        return None

    new_rate = rate
    if len(observations) >= 3 and float(np.ptp(x)) >= 20.0:
        slope, _intercept = np.polyfit(x, residual, 1)
        slope = float(slope)
        if abs(slope) <= 0.006:
            new_rate = max(0.75, min(1.25, rate * (1.0 + slope)))
            center = max(0.0, float(target_duration_sec)) / 2.0
            old_source_center = source_start + center / rate
            source_start = max(0.0, old_source_center - center / new_rate)

    # Positive residual means the rendered dub is early: move source extraction
    # backwards so the content arrives later on the target timeline.
    source_start = max(0.0, source_start - median / new_rate)
    return {
        "rate": float(new_rate),
        "source_start_sec": float(source_start),
        "median_residual_sec": median,
        "mad_sec": mad,
    }


def _live_progressive(args: argparse.Namespace) -> int:
    try:
        reference = Path(args.live_reference).resolve()
        external = Path(args.live_external).resolve()
        if not reference.is_file() or not external.is_file():
            raise FileNotFoundError("Live Progressive için yerel video ve harici ses gerekli.")

        seed = json.loads(args.live_region_json or "{}")
        if bool(seed.get("missing_source")):
            raise RuntimeError("Türkçe kaynakta bulunmayan bölge lokal senkronlanamaz.")

        target_start = float(seed.get("target_start_sec") or 0.0)
        target_end = float(seed.get("target_end_sec") or target_start)
        source_start = float(seed.get("source_start_sec") or 0.0)
        source_end = float(seed.get("source_end_sec") or source_start)
        mapping_rate = max(0.75, min(1.25, float(seed.get("rate") or 1.0)))
        confidence = float(seed.get("confidence") or 0.0)
        position = max(target_start, min(target_end, float(args.live_position)))
        if target_end - target_start < 12.0:
            raise RuntimeError("Live Progressive bölgesi lokal doğrulama için çok kısa.")

        # Current position gets roughly 20 s of history and up to 70 s of look-ahead.
        local_start = max(target_start, position - 20.0)
        local_end = min(target_end, position + 70.0)
        if local_end - local_start < 28.0:
            center = position
            local_start = max(target_start, center - 20.0)
            local_end = min(target_end, local_start + 55.0)
            local_start = max(target_start, local_end - 55.0)
        local_duration = local_end - local_start
        if local_duration < 16.0:
            raise RuntimeError("Live Progressive için yeterli lokal zaman aralığı yok.")

        predicted_source_start = source_start + (local_start - target_start) / mapping_rate
        _emit_progress(10)
        _emit_log(
            f"Live Progressive lokal analiz: hedef {local_start:.1f}-{local_end:.1f} sn · "
            f"rate {mapping_rate:.6f}."
        )

        with tempfile.TemporaryDirectory(prefix="altyazidb-live-progressive-") as td:
            root = Path(td)
            ref_pcm = root / "reference.s16le"
            ext_pcm = root / "external.s16le"
            _decode_pcm_range(reference, ref_pcm, HYBRID_PCM_RATE, local_start, local_duration)
            _emit_progress(35)
            _render_live_external_pcm(
                external, ext_pcm, HYBRID_PCM_RATE,
                predicted_source_start, local_duration, mapping_rate,
            )
            _emit_progress(55)
            first = _verify_live_progressive_pair(ref_pcm, ext_pcm, HYBRID_PCM_RATE)

            final = first
            final_rate = mapping_rate
            final_source_start = predicted_source_start
            observations = [
                (float(item["target_sec"]), float(item["residual_sec"]))
                for item in (first.get("observations") or [])
            ]
            refinement = _live_refine_mapping_from_observations(
                mapping_rate, predicted_source_start, local_duration, observations
            )
            if refinement is not None and (
                first.get("p75_ms") is None or float(first.get("p75_ms") or 0.0) > 120.0
            ):
                final_rate = float(refinement["rate"])
                final_source_start = float(refinement["source_start_sec"])
                _emit_log(
                    f"Live Progressive ikinci geçiş: rate {final_rate:.6f} · "
                    f"lokal kaynak kaydırma {final_source_start - predicted_source_start:+.3f} sn."
                )
                _render_live_external_pcm(
                    external, ext_pcm, HYBRID_PCM_RATE,
                    final_source_start, local_duration, final_rate,
                )
                _emit_progress(78)
                final = _verify_live_progressive_pair(ref_pcm, ext_pcm, HYBRID_PCM_RATE)

        p75_ms = final.get("p75_ms")
        probes = int(final.get("count") or 0)
        mad_ms = final.get("mad_ms")
        mean_score = float(final.get("mean_score") or 0.0)
        trusted = bool(
            p75_ms is not None and probes >= 2 and float(p75_ms) <= 180.0 and
            mad_ms is not None and float(mad_ms) <= 180.0 and
            mean_score >= 0.10
        )

        # Convert the local calibrated source point back to the full seed-region map.
        new_source_start = final_source_start - (local_start - target_start) / final_rate
        new_source_start = max(0.0, new_source_start)
        new_source_end = max(
            new_source_start + 0.05,
            new_source_start + max(0.05, target_end - target_start) / final_rate,
        )
        verified_ms = None if p75_ms is None else float(p75_ms)
        updated_confidence = max(confidence, min(1.0, mean_score))

        region = {
            "target_start_sec": target_start,
            "target_end_sec": target_end,
            "source_start_sec": new_source_start,
            "source_end_sec": new_source_end,
            "rate": final_rate,
            "confidence": updated_confidence,
            "missing_source": False,
            "plan_residual_ms": seed.get("plan_residual_ms"),
            "verified_residual_ms": verified_ms,
            "verified_probes": probes,
            "trusted": trusted,
        }
        _emit_progress(100)
        _emit_log(
            "Live Progressive lokal sonuç: " +
            (f"{verified_ms:.1f} ms / {probes} probe · güvenli." if trusted and verified_ms is not None
             else f"{verified_ms:.1f} ms / {probes} probe · henüz güvenli değil." if verified_ms is not None
             else "yeterli ortak ses olayı bulunamadı.")
        )
        _emit({
            "ok": True,
            "event": "completed",
            "engine": "AudioSyncTool+LiveProgressive",
            "engine_version": AUDIOSYNCTOOL_VERSION,
            "worker_version": WORKER_VERSION,
            "live_progressive": True,
            "region": region,
        })
        return 0
    except Exception as exc:
        _emit({
            "ok": False,
            "event": "failed",
            "engine": "AudioSyncTool+LiveProgressive",
            "worker_version": WORKER_VERSION,
            "error": str(exc),
            "error_type": type(exc).__name__,
        })
        traceback.print_exc(file=sys.stderr)
        return 1


def _progressive_regions_from_plan(plan: dict, verification: dict) -> list[dict]:
    """Build safe live-sync regions from the already verified hard-cut timeline.

    A global hard-cut render may fail the 180 ms gate even though several long
    regions are individually excellent. Progressive Adaptive Sync exposes only
    those regions which have their own local evidence. A bad region therefore
    falls back to the embedded/reference audio instead of invalidating the
    entire dub.
    """
    import numpy as np

    observations = list(verification.get("observations") or [])
    grouped: dict[int, list[float]] = {}
    for item in observations:
        grouped.setdefault(int(item["region_index"]), []).append(float(item["residual_sec"]))

    result = []
    matched_index = -1
    for region in sorted(plan.get("regions") or [], key=lambda item: float(item.get("target_start_sec") or 0.0)):
        missing = bool(region.get("missing_source"))
        values = []
        if not missing:
            matched_index += 1
            values = grouped.get(matched_index) or []

        verified_ms = None
        verified_probes = len(values)
        if values:
            verified_ms = float(np.percentile(np.abs(np.asarray(values, dtype=np.float64)), 75) * 1000.0)

        plan_residual = region.get("residual_ms")
        plan_residual_ms = None if plan_residual is None else abs(float(plan_residual))
        confidence = float(region.get("confidence") or 0.0)
        rate = float(region.get("rate") or 1.0)

        trusted = False
        if not missing and 0.75 <= rate <= 1.25:
            if verified_ms is not None and verified_probes >= 1:
                trusted = verified_ms <= 180.0 and confidence >= 0.045
            elif plan_residual_ms is not None:
                trusted = plan_residual_ms <= 120.0 and confidence >= 0.35

        result.append({
            "target_start_sec": float(region.get("target_start_sec") or 0.0),
            "target_end_sec": float(region.get("target_end_sec") or 0.0),
            "source_start_sec": float(region.get("source_start_sec") or 0.0),
            "source_end_sec": float(region.get("source_end_sec") or 0.0),
            "rate": rate,
            "confidence": confidence,
            "missing_source": missing,
            "plan_residual_ms": plan_residual_ms,
            "verified_residual_ms": verified_ms,
            "verified_probes": int(verified_probes),
            "trusted": bool(trusted),
        })

    return result

def _render_hybrid(reference: Path, external: Path, output: Path, plan: dict,
                   on_progress=None, on_log=None, allow_post_calibration: bool = True) -> dict:
    channels, _source_rate = _probe_audio(external)
    output_rate = 48000
    regions = sorted(plan.get("regions") or [], key=lambda item: item["target_start_sec"])
    ref_duration = float(plan["reference_duration_sec"])
    if not regions:
        raise RuntimeError("Hybrid Recovery geçerli senkron bölgesi oluşturamadı.")

    with tempfile.TemporaryDirectory(prefix="altyazidb-hybrid-render-") as td:
        temp = Path(td)
        timeline: list[dict] = []
        cursor = 0.0
        for region in regions:
            start = max(0.0, min(ref_duration, float(region["target_start_sec"])))
            end = max(start, min(ref_duration, float(region["target_end_sec"])))
            if end - start < 0.10:
                continue
            if start > cursor + 0.08:
                timeline.append({"kind": "reference", "start": cursor, "end": start})
            if start < cursor:
                start = cursor
            if end <= start + 0.05:
                continue
            if region.get("missing_source"):
                timeline.append({"kind": "reference", "start": start, "end": end})
            else:
                rate = float(region.get("rate") or 1.0)
                source_start = float(region.get("source_start_sec") or 0.0)
                source_end = float(region.get("source_end_sec") or source_start)
                # If target start was trimmed because of overlap, move source start using the local map.
                original_target_start = float(region["target_start_sec"])
                if start > original_target_start and rate > 0:
                    source_start += (start - original_target_start) / rate
                target_duration = end - start
                source_duration = max(0.05, source_end - source_start)
                expected_source_duration = target_duration / max(rate, 1e-6)
                source_duration = min(source_duration, max(0.05, expected_source_duration))
                timeline.append({
                    "kind": "external",
                    "start": start,
                    "end": end,
                    "source_start": source_start,
                    "source_duration": source_duration,
                    "rate": rate,
                })
            cursor = max(cursor, end)
        if cursor < ref_duration - 0.08:
            timeline.append({"kind": "reference", "start": cursor, "end": ref_duration})

        segment_paths: list[Path] = []
        count = len(timeline)
        if count == 0:
            raise RuntimeError("Hybrid Recovery çıktı zaman çizelgesi boş kaldı.")

        for index, segment in enumerate(timeline):
            target_duration = max(0.05, segment["end"] - segment["start"])
            segment_path = temp / f"segment-{index:04d}.flac"
            if segment["kind"] == "external":
                source_duration = max(0.05, float(segment["source_duration"]))
                tempo = source_duration / target_duration
                filters = _atempo_chain(tempo)
                filters += f",apad=pad_dur={target_duration:.6f},atrim=duration={target_duration:.6f},asetpts=N/SR/TB"
                command = [
                    "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-ss", f"{segment['source_start']:.6f}", "-t", f"{source_duration:.6f}",
                    "-i", str(external), "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-af", filters, "-ar", str(output_rate), "-ac", str(channels),
                    "-c:a", "flac", "-compression_level", "0", str(segment_path),
                ]
            else:
                filters = f"apad=pad_dur={target_duration:.6f},atrim=duration={target_duration:.6f},asetpts=N/SR/TB"
                command = [
                    "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-ss", f"{segment['start']:.6f}", "-t", f"{target_duration:.6f}",
                    "-i", str(reference), "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-af", filters, "-ar", str(output_rate), "-ac", str(channels),
                    "-c:a", "flac", "-compression_level", "0", str(segment_path),
                ]
            _run_checked(command)
            segment_paths.append(segment_path)
            if on_progress:
                on_progress(58 + int(((index + 1) / count) * 34))

        concat_file = temp / "concat.txt"
        concat_file.write_text(
            "".join(f"file '{_escape_concat(path)}'\n" for path in segment_paths),
            encoding="utf-8",
        )
        _run_checked([
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "concat", "-safe", "0", "-i", str(concat_file),
            "-t", f"{ref_duration:.6f}", "-c:a", "flac", "-compression_level", "5", str(output),
        ])

    if not output.is_file() or output.stat().st_size < 4096:
        raise RuntimeError("Hybrid Recovery senkronlu FLAC çıktısını oluşturamadı.")
    if on_progress:
        on_progress(95)
    verification = _verify_hybrid_output_detailed(reference, output, plan)
    verified_residual_ms = verification.get("p75_ms")
    verified_probes = int(verification.get("count") or 0)

    if on_log:
        verification_text = (
            "çıktı residual ölçülemedi"
            if verified_residual_ms is None
            else (
                f"çıktı residual p75 {verified_residual_ms:.1f} ms / {verified_probes} probe"
                f" · signed median {float(verification.get('signed_median_ms') or 0.0):+.1f} ms"
                f" · MAD {float(verification.get('mad_ms') or 0.0):.1f} ms"
            )
        )
        on_log(
            f"Hybrid Edit Recovery: {len([r for r in regions if not r.get('missing_source')])} eşleşen bölge, "
            f"{plan.get('missing_regions', 0)} eksik bölge, kapsama %{plan.get('coverage', 0.0) * 100:.1f}, "
            f"{verification_text}."
        )

    # Audition-style second pass: if the rendered timeline is structurally good
    # but carries one consistent sub-second residual, feed that measured waveform
    # error back into each region once and render again. Never loop indefinitely.
    if (
        allow_post_calibration and
        verified_residual_ms is not None and
        180.0 < float(verified_residual_ms) <= 1200.0 and
        verified_probes >= 2
    ):
        calibrated_plan, corrections_ms = _post_render_calibrated_plan(plan, verification)
        if calibrated_plan is not None:
            if on_log:
                preview = ", ".join(f"{value:+.0f}" for value in corrections_ms[:6])
                on_log(
                    "Post-render Waveform Calibration uygulanıyor: "
                    f"{len(corrections_ms)} bölge · düzeltmeler [{preview}] ms."
                )
            calibrated = _render_hybrid(
                reference,
                external,
                output,
                calibrated_plan,
                on_progress,
                on_log,
                allow_post_calibration=False,
            )
            calibrated["post_render_calibration"] = True
            calibrated["post_render_corrections_ms"] = corrections_ms
            return calibrated

    progressive_regions = _progressive_regions_from_plan(plan, verification)
    trusted_regions = len([region for region in progressive_regions if region.get("trusted")])
    if on_log and progressive_regions:
        on_log(
            f"Progressive Adaptive Sync haritası hazır: {trusted_regions}/"
            f"{len([r for r in progressive_regions if not r.get('missing_source')])} "
            "Türkçe bölge yerel olarak doğrulandı."
        )

    return {
        "output_path": str(output),
        "verified_residual_ms": verified_residual_ms,
        "verified_probes": int(verified_probes),
        "regions_applied": len([r for r in regions if not r.get("missing_source")]),
        "missing_regions": int(plan.get("missing_regions") or 0),
        "hybrid_confidence": float(plan.get("confidence") or 0.0),
        "hybrid_coverage": float(plan.get("coverage") or 0.0),
        "post_render_calibration": False,
        "post_render_corrections_ms": [],
        "progressive_regions": progressive_regions,
    }


def _run_hybrid_recovery(reference: Path, external: Path, output: Path, analysis,
                         on_progress=None, on_log=None) -> dict:
    if on_log:
        on_log("Zorlu farklı kurgu algılandı; Hybrid Edit Recovery başlatılıyor.")
    if on_progress:
        on_progress(42)
    with tempfile.TemporaryDirectory(prefix="altyazidb-hybrid-analysis-") as td:
        root = Path(td)
        ref_pcm = root / "reference.s16le"
        ext_pcm = root / "external.s16le"
        _decode_pcm(reference, ref_pcm, HYBRID_PCM_RATE)
        if on_progress:
            on_progress(47)
        _decode_pcm(external, ext_pcm, HYBRID_PCM_RATE)
        if on_progress:
            on_progress(52)
        plan = _hybrid_plan(ref_pcm, ext_pcm, HYBRID_PCM_RATE, analysis)
    if not plan.get("usable"):
        raise RuntimeError(
            "Hybrid Edit Recovery yeterli güvenilir eşleşme çıkaramadı "
            f"(kapsama %{float(plan.get('coverage') or 0) * 100:.1f}, "
            f"güven {float(plan.get('confidence') or 0):.2f})."
        )
    if on_log:
        on_log(
            f"Hybrid plan hazır: {int(plan.get('anchors') or 0)} anchor, "
            f"{len(plan.get('regions') or [])} bölge, "
            f"{int(plan.get('missing_regions') or 0)} eksik hedef bölge."
        )
    return _render_hybrid(reference, external, output, plan, on_progress, on_log)


def _analysis_hint_from_args(args: argparse.Namespace):
    if args.hint_delay_ms is None and not args.hint_regions_json:
        return None
    regions = []
    if args.hint_regions_json:
        try:
            payload = json.loads(args.hint_regions_json)
            for item in payload if isinstance(payload, list) else []:
                start = float(item.get("start_sec", 0.0))
                end = float(item.get("end_sec", start))
                lag = float(item.get("lag_ms", 0.0))
                if math.isfinite(start) and math.isfinite(end) and math.isfinite(lag) and end > start:
                    regions.append(SimpleNamespace(
                        start_sec=start, end_sec=end, lag_ms=lag,
                        window_count=0, confidence=0.0))
        except Exception as exc:
            _emit_log(f"Hybrid AST hint bölgeleri okunamadı: {exc}")
    return SimpleNamespace(
        delay_ms=float(args.hint_delay_ms or 0.0),
        confidence=float(args.hint_confidence or 0.0),
        offset_regions=regions,
        verdict=SimpleNamespace(value="uncertain"),
    )


def _choose_hybrid_analysis(primary, hint):
    if primary is None:
        return hint
    primary_regions = list(getattr(primary, "offset_regions", ()) or ())
    hint_regions = list(getattr(hint, "offset_regions", ()) or ()) if hint is not None else []
    # The pre-analysis result is especially valuable when SyncPipeline aborted on
    # NoMatch before preserving its analysis object. If both exist, keep the map
    # with more edit regions; otherwise prefer the upstream full-sync analysis.
    if len(hint_regions) > len(primary_regions):
        return hint
    return primary


def _full_sync(args: argparse.Namespace) -> int:
    """Run upstream correction first, then hard-cut recovery when it is doubtful."""
    _bootstrap_source()
    output = Path(args.sync_output).resolve() if args.sync_output else None
    try:
        from audio_sync.config import EncodingPipeline, FFmpegEncodeConfig, FFmpegOutputFormat
        from audio_sync.core.pipeline import EncodingRequest, SyncPipeline, SyncRequest

        reference = Path(args.sync_reference).resolve()
        external = Path(args.sync_external).resolve()
        output = Path(args.sync_output).resolve()
        analysis_hint = _analysis_hint_from_args(args)
        if not reference.is_file():
            raise FileNotFoundError("Referans video/ses dosyası bulunamadı.")
        if not external.is_file():
            raise FileNotFoundError("Senkronize edilecek harici ses yerel dosya değil.")
        output.parent.mkdir(parents=True, exist_ok=True)

        def on_progress(value: int) -> None:
            # Keep headroom for Hybrid Recovery if upstream is doubtful.
            _emit_progress(min(40, int(max(0, min(100, value)) * 0.40)))

        def on_log(message: str) -> None:
            _emit_log(message)

        request = SyncRequest(
            source_path=str(reference),
            sync_path=str(external),
            output_path=str(output),
            skip_intro_sec=float(args.skip_intro),
            total_segments=int(args.segments),
            correct_drift=True,
            correct_steps=True,
            auto_fps_conversion=True,
            allow_no_match=False,
            encoding=EncodingRequest(
                pipeline=EncodingPipeline.FFMPEG,
                ffmpeg=FFmpegEncodeConfig(
                    format=FFmpegOutputFormat.FLAC,
                    flac_compression=5,
                    flac_bit_depth=16,
                ),
            ),
        )

        outcome = None
        analysis = None
        upstream_error = None
        try:
            outcome = SyncPipeline().run(request, on_progress=on_progress, on_log=on_log)
            analysis = outcome.analysis
        except Exception as exc:
            upstream_error = exc
            _emit_log(f"AST tam senkron tamamlanamadı: {exc}")

        if outcome is not None:
            verdict = str(getattr(getattr(analysis, "verdict", None), "value", "uncertain")).lower()
            confidence = float(getattr(analysis, "confidence", 0.0) or 0.0)
            regions = list(getattr(analysis, "offset_regions", ()) or ())
            residual = outcome.verified_residual_ms
            upstream_good = (
                verdict == "reliable" and
                (residual is None or abs(float(residual)) <= 80.0)
            )
            hard_edit = len(regions) > 1 and (confidence < 1.0 or verdict != "reliable")
            needs_hybrid = (not upstream_good) or hard_edit

            if not needs_hybrid:
                _emit_progress(100)
                _emit({
                    "ok": True,
                    "event": "completed",
                    "engine": "AudioSyncTool",
                    "engine_version": AUDIOSYNCTOOL_VERSION,
                    "worker_version": WORKER_VERSION,
                    "output_path": outcome.output_path,
                    "verified_residual_ms": _json_safe(outcome.verified_residual_ms),
                    "verified_probes": int(outcome.verified_probes or 0),
                    "drift_applied_ms_per_min": _json_safe(outcome.drift_applied_ms_per_min),
                    "fps_conversion_applied": getattr(outcome.fps_conversion_applied, "name", None),
                    "regions_applied": len(outcome.offset_regions_applied or ()),
                    "verdict": verdict,
                    "delay_ms": float(getattr(analysis, "delay_ms", 0.0)),
                    "confidence": confidence,
                    "hybrid_recovery": False,
                    "hybrid_missing_regions": 0,
                    "hybrid_confidence": 0.0,
                    "hybrid_coverage": 0.0,
                    "recovery_kind": "ast",
                    "timeline_anchors": 0,
                    "visual_anchors": 0,
                    "waveform_anchors": 0,
                    "dtw_regions": 0,
                "progressive_regions": hybrid.get("progressive_regions") or [],
                })
                return 0

        # Upstream failed or remained doubtful. Rev10.8 is audio-first:
        # raw dub files always get a waveform timeline attempt. If the dub source
        # also has video, visual edit anchors become an optional second modality.
        recovery_errors: list[str] = []
        hybrid_analysis = _choose_hybrid_analysis(analysis, analysis_hint)
        if hybrid_analysis is analysis_hint and analysis_hint is not None:
            _emit_log(
                f"Multi-Modal Sync ilk AST ipuçlarını kullanıyor: "
                f"{len(getattr(analysis_hint, 'offset_regions', ()) or ())} bölge, "
                f"ofset {float(getattr(analysis_hint, 'delay_ms', 0.0)):+.1f} ms."
            )

        has_reference_video = _probe_video_duration(reference) is not None
        has_external_video = _probe_video_duration(external) is not None
        if has_reference_video and has_external_video:
            try:
                if output.exists():
                    output.unlink(missing_ok=True)
                multi = _run_multimodal_recovery(
                    reference, external, output, hybrid_analysis, _emit_progress, _emit_log
                )
                _emit_progress(100)
                _emit({
                    "ok": True,
                    "event": "completed",
                    "engine": "AudioSyncTool+MultiModal",
                    "engine_version": AUDIOSYNCTOOL_VERSION,
                    "worker_version": WORKER_VERSION,
                    "output_path": multi["output_path"],
                    "verified_residual_ms": _json_safe(multi.get("verified_residual_ms")),
                    "verified_probes": int(multi.get("verified_probes") or 0),
                    "drift_applied_ms_per_min": None,
                    "fps_conversion_applied": None,
                    "regions_applied": int(multi.get("regions_applied") or 0),
                    "verdict": "reliable",
                    "delay_ms": float(getattr(hybrid_analysis, "delay_ms", 0.0) or 0.0) if hybrid_analysis is not None else 0.0,
                    "confidence": float(multi.get("hybrid_confidence") or 0.0),
                    "hybrid_recovery": True,
                    "hybrid_missing_regions": int(multi.get("missing_regions") or 0),
                    "hybrid_confidence": float(multi.get("hybrid_confidence") or 0.0),
                    "hybrid_coverage": float(multi.get("hybrid_coverage") or 0.0),
                    "recovery_kind": str(multi.get("recovery_kind") or "multimodal"),
                    "timeline_anchors": int(multi.get("timeline_anchors") or 0),
                    "visual_anchors": int(multi.get("visual_anchors") or 0),
                    "waveform_anchors": int(multi.get("waveform_anchors") or 0),
                    "dtw_regions": int(multi.get("dtw_regions") or 0),
                    "progressive_regions": multi.get("progressive_regions") or [],
                })
                return 0
            except Exception as multi_exc:
                recovery_errors.append(f"Multi-Modal: {multi_exc}")
                _emit_log(f"Multi-Modal Timeline Sync tamamlanamadı; audio-only timeline deneniyor: {multi_exc}")

        # Always attempt the audio-only timeline mapper. This is the primary path
        # for raw AC3/EAC3/AAC/FLAC/WAV/MKA dub files and is also a robust fallback
        # when visual matching is unavailable.
        try:
            if output.exists():
                output.unlink(missing_ok=True)
            audio_timeline = _run_audio_timeline_recovery(
                reference, external, output, hybrid_analysis, _emit_progress, _emit_log
            )
            _emit_progress(100)
            _emit({
                "ok": True,
                "event": "completed",
                "engine": "AudioSyncTool+WaveformTimeline",
                "engine_version": AUDIOSYNCTOOL_VERSION,
                "worker_version": WORKER_VERSION,
                "output_path": audio_timeline["output_path"],
                "verified_residual_ms": _json_safe(audio_timeline.get("verified_residual_ms")),
                "verified_probes": int(audio_timeline.get("verified_probes") or 0),
                "drift_applied_ms_per_min": None,
                "fps_conversion_applied": None,
                "regions_applied": int(audio_timeline.get("regions_applied") or 0),
                "verdict": "reliable",
                "delay_ms": float(getattr(hybrid_analysis, "delay_ms", 0.0) or 0.0) if hybrid_analysis is not None else 0.0,
                "confidence": float(audio_timeline.get("hybrid_confidence") or 0.0),
                "hybrid_recovery": True,
                "hybrid_missing_regions": int(audio_timeline.get("missing_regions") or 0),
                "hybrid_confidence": float(audio_timeline.get("hybrid_confidence") or 0.0),
                "hybrid_coverage": float(audio_timeline.get("hybrid_coverage") or 0.0),
                "recovery_kind": "audio_timeline",
                "timeline_anchors": int(audio_timeline.get("timeline_anchors") or 0),
                "visual_anchors": 0,
                "waveform_anchors": int(audio_timeline.get("waveform_anchors") or 0),
                "dtw_regions": int(audio_timeline.get("dtw_regions") or 0),
                "progressive_regions": audio_timeline.get("progressive_regions") or [],
            })
            return 0
        except Exception as audio_timeline_exc:
            recovery_errors.append(f"Waveform Timeline: {audio_timeline_exc}")
            _emit_log(f"Waveform Timeline Mapper tamamlanamadı; legacy Hybrid deneniyor: {audio_timeline_exc}")

        try:
            if output.exists():
                output.unlink(missing_ok=True)
            hybrid = _run_hybrid_recovery(reference, external, output, hybrid_analysis, _emit_progress, _emit_log)
            _emit_progress(100)
            _emit({
                "ok": True,
                "event": "completed",
                "engine": "AudioSyncTool+Hybrid",
                "engine_version": AUDIOSYNCTOOL_VERSION,
                "worker_version": WORKER_VERSION,
                "output_path": hybrid["output_path"],
                "verified_residual_ms": _json_safe(hybrid.get("verified_residual_ms")),
                "verified_probes": int(hybrid.get("verified_probes") or 0),
                "drift_applied_ms_per_min": None,
                "fps_conversion_applied": None,
                "regions_applied": int(hybrid.get("regions_applied") or 0),
                "verdict": "reliable",
                "delay_ms": float(getattr(hybrid_analysis, "delay_ms", 0.0) or 0.0) if hybrid_analysis is not None else 0.0,
                "confidence": float(hybrid.get("hybrid_confidence") or 0.0),
                "hybrid_recovery": True,
                "hybrid_missing_regions": int(hybrid.get("missing_regions") or 0),
                "hybrid_confidence": float(hybrid.get("hybrid_confidence") or 0.0),
                "hybrid_coverage": float(hybrid.get("hybrid_coverage") or 0.0),
                "recovery_kind": "audio",
                "timeline_anchors": int(hybrid.get("regions_applied") or 0),
                "visual_anchors": 0,
                "waveform_anchors": int(hybrid.get("regions_applied") or 0),
                "dtw_regions": 0,
            })
            return 0
        except Exception as hybrid_exc:
            detail = f"Hybrid Edit Recovery başarısız: {hybrid_exc}"
            if recovery_errors:
                detail += " | " + " | ".join(recovery_errors)
            if upstream_error is not None:
                detail += f" | AST: {upstream_error}"
            raise RuntimeError(detail) from hybrid_exc

    except Exception as exc:
        try:
            if output is not None:
                output.unlink(missing_ok=True)
        except OSError:
            pass
        _emit({
            "ok": False,
            "event": "failed",
            "engine": "AudioSyncTool",
            "engine_version": AUDIOSYNCTOOL_VERSION,
            "worker_version": WORKER_VERSION,
            "error": str(exc),
            "error_type": type(exc).__name__,
        })
        traceback.print_exc(file=sys.stderr)
        return 1


def main() -> int:
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--reference")
    parser.add_argument("--external")
    parser.add_argument("--rate", type=int, default=16000)
    parser.add_argument("--skip-intro", type=float, default=120.0)
    parser.add_argument("--segments", type=int, default=12)
    parser.add_argument("--sync-reference")
    parser.add_argument("--sync-external")
    parser.add_argument("--sync-output")
    parser.add_argument("--hint-delay-ms", type=float)
    parser.add_argument("--hint-confidence", type=float)
    parser.add_argument("--hint-regions-json")
    parser.add_argument("--live-reference")
    parser.add_argument("--live-external")
    parser.add_argument("--live-position", type=float)
    parser.add_argument("--live-region-json")
    args = parser.parse_args()

    if args.self_test:
        return _self_test()
    if args.live_reference or args.live_external or args.live_region_json:
        if not args.live_reference or not args.live_external or args.live_position is None or not args.live_region_json:
            parser.error("--live-reference, --live-external, --live-position ve --live-region-json birlikte zorunludur")
        return _live_progressive(args)
    if args.sync_reference or args.sync_external or args.sync_output:
        if not args.sync_reference or not args.sync_external or not args.sync_output:
            parser.error("--sync-reference, --sync-external ve --sync-output birlikte zorunludur")
        return _full_sync(args)
    if not args.reference or not args.external:
        parser.error("--reference ve --external zorunludur")
    return _analyze(args)


if __name__ == "__main__":
    raise SystemExit(main())

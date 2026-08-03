# FAQ

**What is PalmierWin?**

PalmierWin is a Windows-native AI video editor built on the open source [Palmier Pro](https://github.com/palmier-io/palmier-pro) codebase. The editor core, timeline model, and product design come from upstream; the Windows app — C#/Avalonia shell, Vulkan compositor, FFmpeg media engine — is built here.

**Is it the same as Palmier Pro?**

No. Palmier Pro is the macOS app by Palmier, Inc. (macOS 26, Apple Silicon). PalmierWin is an independent fork for Windows, by [@yassinebkr](https://github.com/yassinebkr). The upstream macOS app still lives in this repo (`Sources/`) and builds unchanged, but nothing here is affiliated with or endorsed by Palmier, Inc.

**What works on Windows today?**

The editor: media library, multi-track timeline (filmstrips, waveforms, trims/roll/ripple, keyframes, fades, effects, LUTs), preview with playback, inspector, project files, and export to H.264/MP4. The AI side: multi-provider agent chat that edits the timeline (Anthropic, OpenAI, Z.AI, Moonshot, OpenRouter), and video generation via Replicate and fal.ai (Seedance 2.0, Kling 3.0, Veo 3) including transitions generated from timeline cuts.

**What's missing?**

Real gaps remain: masking, transitions-as-effects, captions/transcription, multicam, beats, scrub audio parity, and the polish of a shipping product. See the repository issues/PRs for the current state.

**Is it free?**

The editor is free and open source under GPLv3. Agent and generation features need your own provider API keys (Anthropic, OpenAI, Replicate, fal.ai, …) — you pay the provider directly.

**What platforms does it support?**

Windows 10/11 x64 with a Vulkan-capable GPU. The macOS app (upstream's) requires macOS 26 on Apple Silicon.

**Why does the repo also contain the Mac app?**

The Windows app shares `PalmierCore` (the portable editor model and engines) with the macOS app, and staying merge-compatible with upstream keeps years of upstream improvements available to the fork. Both sides build in CI.

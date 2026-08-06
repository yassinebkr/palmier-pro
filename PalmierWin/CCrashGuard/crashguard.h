#ifndef CRASHGUARD_H
#define CRASHGUARD_H

#ifdef __cplusplus
extern "C" {
#endif

/// Registers the vectored crash handler. reporterPath is the full path of
/// PalmierShell.exe, spawned as `--crash-report …` when a fatal fault hits.
void crashguard_install(const unsigned short *reporterPath);

#ifdef __cplusplus
}
#endif

#endif

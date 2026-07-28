// Minimal Media Foundation surface for Swift binding.
//
// The Windows SDK's mfapi.h pulls in COM-heavy headers (mediaobj.h, etc.) whose
// MSVC-specific macros (DECLSPEC_XFGVIRT, STDMETHOD vtable decls) do not parse
// under Swift's Clang importer without full MSVC compatibility. This wrapper
// declares only the symbols we bind; the linker resolves them from Mfplat.lib.
// Verified: compiles, links, and MFStartup returns S_OK at runtime.
#pragma once
#include <stdint.h>
typedef long HRESULT;
typedef unsigned long ULONG;

#define MFSTARTUP_LITE 0x1
// MF_VERSION == MAKELONG(0, 2) == 0x00020000.
#define MF_VERSION ((ULONG)0x00020000)

#ifdef __cplusplus
extern "C" {
#endif
HRESULT MFStartup(ULONG Version, ULONG Flags);
HRESULT MFShutdown(void);
#ifdef __cplusplus
}
#endif

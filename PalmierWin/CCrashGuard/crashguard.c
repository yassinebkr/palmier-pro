// Vectored crash handler in pure native code. A managed handler re-entered
// the CLR on arbitrary threads — under GC that is a fatal reverse-pinvoke
// transition (0x80131506) that killed the app during ordinary playback.
// Here nothing managed ever runs on the faulting thread: on a fatal code we
// spawn the shell as a crash reporter (a healthy process) which writes the
// .log and the minidump, then let the process die its normal death.

#include <windows.h>
#include <stdio.h>

static WCHAR g_reporterPath[MAX_PATH];
static ULONGLONG g_installedTick;
static volatile LONG g_reported;

static int crashguard_is_fatal(DWORD code) {
    switch (code) {
    case 0xC0000005:  // access violation
    case 0xC000001D:  // illegal instruction (Swift traps land here)
    case 0xC00000FD:  // stack overflow
    case 0xC0000094:  // integer divide by zero
    case 0xC0000374:  // heap corruption
    case 0xC0000409:  // stack cookie / fast fail
    case 0x80131506:  // CLR execution engine failure
    case 0xE06D7363:  // C++ exception escaping native code
        return 1;
    default:
        return 0;
    }
}

static LONG WINAPI crashguard_handler(PEXCEPTION_POINTERS ep) {
    if (!ep || !ep->ExceptionRecord ||
        !crashguard_is_fatal(ep->ExceptionRecord->ExceptionCode))
        return EXCEPTION_CONTINUE_SEARCH;
    if (InterlockedExchange(&g_reported, 1) != 0)
        return EXCEPTION_CONTINUE_SEARCH;

    WCHAR cmd[2048];
    _snwprintf_s(cmd, 2048, _TRUNCATE,
        L"\"%ls\" --crash-report %lu %lu 0x%llx 0x%08lX 0x%llx %llu",
        g_reporterPath,
        GetCurrentProcessId(),
        GetCurrentThreadId(),
        (unsigned long long)(uintptr_t)ep,
        ep->ExceptionRecord->ExceptionCode,
        (unsigned long long)(uintptr_t)ep->ExceptionRecord->ExceptionAddress,
        GetTickCount64() - g_installedTick);

    STARTUPINFOW si;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi;
    if (CreateProcessW(g_reporterPath, cmd, NULL, NULL, FALSE,
                       CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
        if (WaitForSingleObject(pi.hProcess, 15000) == WAIT_TIMEOUT)
            TerminateProcess(pi.hProcess, 1);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

void crashguard_install(const unsigned short *reporterPath) {
    if (!reporterPath)
        return;
    wcsncpy_s(g_reporterPath, MAX_PATH, (const WCHAR *)reporterPath, _TRUNCATE);
    g_installedTick = GetTickCount64();
    AddVectoredExceptionHandler(1, crashguard_handler);
}

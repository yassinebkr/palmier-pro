namespace PalmierShell.Tests;

/// On-access scanners briefly hold brand-new temp files, which used to flake
/// settings-file tests with sharing-violation IOExceptions. Retry that one
/// transient; anything else fails immediately.
static class TempFiles {
    const int SharingViolation = unchecked((int)0x80070020);

    public static void Run(Action action) {
        for (int attempt = 0; ; attempt++) {
            try {
                action();
                return;
            } catch (IOException ex) when (ex.HResult == SharingViolation && attempt < 5) {
                Thread.Sleep(50);
            }
        }
    }
}

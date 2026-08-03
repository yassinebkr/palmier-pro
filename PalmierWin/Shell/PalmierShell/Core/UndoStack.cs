namespace PalmierShell.Core;

/// Snapshot-based undo: each executed intent stores the timeline JSON before
/// and after, so undo/redo restore exact state. One user intent = one entry;
/// failed intents create no entry.
///
/// Entries also record which timeline tab they were made on. Undoing an edit
/// from another tab switches to that tab first, so a restore can never write
/// one timeline's snapshot over another's.
public sealed class UndoStack {
    public sealed record Entry(string Name, string BeforeJson, string AfterJson, int Scope);

    readonly Stack<Entry> undo = new();
    readonly Stack<Entry> redo = new();
    readonly Func<string> capture;
    readonly Func<string, bool> restore;
    readonly Func<int> scope;
    readonly Func<int, bool> enterScope;

    public event Action? Changed;

    public UndoStack(Func<string> capture, Func<string, bool> restore,
                     Func<int>? scope = null, Func<int, bool>? enterScope = null) {
        this.capture = capture;
        this.restore = restore;
        this.scope = scope ?? (() => 0);
        this.enterScope = enterScope ?? (_ => true);
    }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// Runs `intent`; on success records one entry (unless the state did not
    /// change) and clears the redo stack. Returns the intent's result.
    public bool Execute(string name, Func<bool> intent) {
        string before = capture();
        int at = scope();
        if (!intent()) return false;
        string after = capture();
        if (after == before) return true;
        undo.Push(new Entry(name, before, after, at));
        redo.Clear();
        Changed?.Invoke();
        return true;
    }

    /// Records an entry for a change that already happened outside Execute
    /// (e.g. one whole agent turn). No-ops when the snapshots are identical.
    public void Push(string name, string beforeJson, string afterJson) {
        if (beforeJson == afterJson) return;
        undo.Push(new Entry(name, beforeJson, afterJson, scope()));
        redo.Clear();
        Changed?.Invoke();
    }

    /// Wipes history — opening or creating a project starts a new one.
    public void Clear() {
        undo.Clear();
        redo.Clear();
        Changed?.Invoke();
    }

    /// Drops history for a timeline that no longer exists and rebases entries
    /// recorded after it, so scopes keep pointing at the right tab.
    public void ForgetScope(int removed) {
        Rebuild(undo, removed);
        Rebuild(redo, removed);
        Changed?.Invoke();
    }

    static void Rebuild(Stack<Entry> stack, int removed) {
        var kept = stack.Where(e => e.Scope != removed)
                        .Select(e => e.Scope > removed ? e with { Scope = e.Scope - 1 } : e)
                        .Reverse().ToArray();
        stack.Clear();
        foreach (var entry in kept) stack.Push(entry);
    }

    public void Undo() {
        if (undo.Count == 0) return;
        var entry = undo.Peek();
        if (!enterScope(entry.Scope)) return;
        undo.Pop();
        if (restore(entry.BeforeJson)) redo.Push(entry);
        Changed?.Invoke();
    }

    public void Redo() {
        if (redo.Count == 0) return;
        var entry = redo.Peek();
        if (!enterScope(entry.Scope)) return;
        redo.Pop();
        if (restore(entry.AfterJson)) undo.Push(entry);
        Changed?.Invoke();
    }
}

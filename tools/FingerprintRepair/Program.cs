// One-shot repair for entries poisoned by the (fixed) Stop-cancellation bug:
// AudioFingerprintError + a non-null empty fingerprint permanently blocked every
// retry gate. Clears the flag and resets the empty fingerprint to null so the
// next scan retries those files; genuinely broken files simply fail once more.
//
// Usage: FingerprintRepair <dbFolder> [--apply]
//   Without --apply: dry run (counts only, no write).
//   VDF must NOT be running — it holds the database in memory and would
//   overwrite the repair on its next checkpoint.

using System.Diagnostics;
using VDF.Core;
using VDF.Core.Utils;

if (args.Length < 1) {
	Console.WriteLine("usage: FingerprintRepair <dbFolder> [--apply]");
	return 1;
}
bool apply = args.Contains("--apply");

// --force: only for repairing a COPY of the database while VDF runs elsewhere.
if (apply && !args.Contains("--force") && Process.GetProcessesByName("VDF.GUI").Length > 0) {
	Console.WriteLine("ABORT: VDF.GUI is running — close it first (it would overwrite the repair).");
	return 1;
}

DatabaseUtils.CustomDatabaseFolder = args[0];
DatabaseUtils.InvalidateDatabaseFolder();
if (!DatabaseUtils.LoadDatabase()) {
	Console.WriteLine("ABORT: database load failed.");
	return 1;
}

string dbFile = Path.Combine(args[0], "ScannedFiles.db");
if (apply && File.Exists(dbFile)) {
	string bak = dbFile + $".bak-{DateTime.Now:yyyyMMdd-HHmmss}";
	File.Copy(dbFile, bak);
	Console.WriteLine($"backup: {bak}");
}

int repaired = 0;
foreach (var e in DatabaseUtils.Database) {
	if (!e.Flags.Has(EntryFlags.AudioFingerprintError)) continue;
	e.Flags.Set(EntryFlags.AudioFingerprintError, false);
	if (e.AudioFingerprint != null && e.AudioFingerprint.Length == 0)
		e.AudioFingerprint = null;
	repaired++;
}

Console.WriteLine($"entries={DatabaseUtils.Database.Count} repaired={repaired} mode={(apply ? "APPLY" : "dry-run")}");
if (apply) {
	DatabaseUtils.SaveDatabase();
	Console.WriteLine("database saved.");
}
return 0;

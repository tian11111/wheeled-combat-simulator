using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sim.Protocol;

namespace Sim.Cli;

/// <summary>
/// SHA-256 canonicalizer for batch result fingerprints. Only stable match
/// fields feed the hashes: UTF-8 bytes, InvariantCulture number formatting
/// ("R" round-trip), lowercase hex output.
///
/// Canonical forms (design-fixed; changing either breaks every previously
/// published fingerprint):
/// <list type="bullet">
///   <item>event fingerprint: the per-event "seq|tick|type|cls|message" lines
///   in commit order, each followed by '\n', hashed as UTF-8.</item>
///   <item>result fingerprint: "seed=", "ticks=", "scoreUs=", "scoreThem=",
///   "penaltyUs=", "penaltyThem=", "done=" lines (invariant "R" numbers) each
///   followed by '\n', then the same event lines, hashed as UTF-8.</item>
/// </list>
/// ReplayHeader.CreatedAt, file paths, thread ids and scheduling order never
/// enter the canonical form, so repeated runs of the same input produce
/// identical fingerprints regardless of parallelism.
/// </summary>
internal static class BatchFingerprint
{
    public static string EventFingerprint(IReadOnlyList<string> eventLines)
        => Sha256Hex(CanonicalEventLines(eventLines));

    public static string ResultFingerprint(
        long seed, long ticks, Scores scores, Scores penalties, string doneReason,
        IReadOnlyList<string> eventLines)
    {
        var canonical = new StringBuilder();
        canonical.Append("seed=").Append(seed).Append('\n');
        canonical.Append("ticks=").Append(ticks).Append('\n');
        canonical.Append("scoreUs=").Append(Inv(scores.Us)).Append('\n');
        canonical.Append("scoreThem=").Append(Inv(scores.Them)).Append('\n');
        canonical.Append("penaltyUs=").Append(Inv(penalties.Us)).Append('\n');
        canonical.Append("penaltyThem=").Append(Inv(penalties.Them)).Append('\n');
        canonical.Append("done=").Append(doneReason).Append('\n');
        canonical.Append(CanonicalEventLines(eventLines));
        return Sha256Hex(canonical.ToString());
    }

    private static string CanonicalEventLines(IReadOnlyList<string> eventLines)
    {
        var canonical = new StringBuilder();
        foreach (var line in eventLines)
        {
            canonical.Append(line).Append('\n');
        }
        return canonical.ToString();
    }

    private static string Inv(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

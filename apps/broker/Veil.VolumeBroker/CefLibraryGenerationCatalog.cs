// SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Veil.VolumeBroker;

// Pure namespace policy for a future append-only library store. An observed
// filename is NOT proof of content, durability, ownership or authenticity.
// No paths, filesystem operations, migration, pruning or startup authority.
internal sealed record CefLibraryGenerationSnapshot(int Committed, int? Pending,
    int? Next, bool RecoveryRequired);

internal static class CefLibraryGenerationCatalog
{
    // Fail closed at the bound until a separately reviewed compaction protocol
    // exists. Do not silently prune or wrap generation numbers.
    internal const int MaximumGenerations = 1024;
    private const string Prefix = "library-";

    internal static string Name(int generation, bool staged = false)
    {
        if (generation <= 0 || generation > MaximumGenerations)
            throw new ArgumentOutOfRangeException(nameof(generation));
        return Prefix + generation.ToString("D19", CultureInfo.InvariantCulture) +
            (staged ? ".stage" : ".json");
    }

    // minimumCommitted must come from a separately trusted anchor, not from
    // the same directory listing. Empty initialization must be explicit.
    // A pending stage always blocks new publication until recovery is reviewed.
    internal static CefLibraryGenerationSnapshot Inspect(IEnumerable<string> names,
        int minimumCommitted, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (minimumCommitted < 0 || minimumCommitted > MaximumGenerations)
            throw new ArgumentOutOfRangeException(nameof(minimumCommitted));
        var committed = new HashSet<int>();
        int? pending = null;
        int count = 0;
        foreach (string name in names)
        {
            if (++count > MaximumGenerations + 1 || name is null ||
                !name.StartsWith(Prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected library generation entry.");
            bool staged = name.EndsWith(".stage", StringComparison.Ordinal);
            string suffix = staged ? ".stage" : ".json";
            if (!name.EndsWith(suffix, StringComparison.Ordinal) ||
                name.Length != Prefix.Length + 19 + suffix.Length ||
                !int.TryParse(name.AsSpan(Prefix.Length, 19), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int generation) ||
                generation <= 0 || generation > MaximumGenerations ||
                !string.Equals(name, Name(generation, staged), StringComparison.Ordinal))
                throw new InvalidDataException("Noncanonical library generation name.");
            if (staged)
            {
                if (pending is not null)
                    throw new InvalidDataException("Multiple pending library generations.");
                pending = generation;
            }
            else if (!committed.Add(generation))
                throw new InvalidDataException("Duplicate library generation.");
        }
        int last = committed.Count;
        for (int i = 1; i <= last; i++)
            if (!committed.Contains(i))
                throw new InvalidDataException("Missing library generation.");
        if (last < minimumCommitted || (last == 0 && !allowEmpty))
            throw new InvalidDataException("Library generation rollback or unexpected empty store.");
        if (pending is not null && (last == MaximumGenerations || pending != last + 1))
            throw new InvalidDataException("Unexpected pending library generation.");
        return new(last, pending, pending is null && last < MaximumGenerations ? last + 1 : null,
            pending is not null);
    }
}

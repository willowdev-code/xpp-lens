namespace XppLens;

/// <summary>Chained receivers ("Table::find(x).name()") resolved from method signatures in the index.</summary>
public sealed partial class Queries
{
    /// <summary>"T" or "T>m1>m2" → the concrete type the chain evaluates to, or null when a step is unknown or primitive.</summary>
    internal static string? ResolveType(Store s, string expr, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(expr, out var hit)) return hit;
        string? result;
        int k = expr.LastIndexOf('>');
        if (k < 0) result = expr;
        else
        {
            var recv = ResolveType(s, expr[..k], cache);
            result = recv == null ? null : ReturnType(s, recv, expr[(k + 1)..]);
        }
        cache[expr] = result;
        return result;
    }

    static string? ReturnType(Store s, string type, string method)
    {
        foreach (var t in new[] { type }.Concat(Ancestors(s, type)))
        {
            // Own methods first, then methods added by extension classes ([ExtensionOf] → target).
            // Two lookups instead of "name = $t OR target = $t", so each one uses its own index.
            foreach (var column in new[] { "name", "target" })
            {
                var sig = s.Scalar($"""
                    SELECT m.sig FROM objects o JOIN methods m ON m.object_id = o.id
                    WHERE o.{column} = $t AND m.name = $m
                      AND o.type IN ('AxClass','AxTable','AxView','AxDataEntityView','AxMap','AxForm')
                    ORDER BY {TypePriority} LIMIT 1
                    """, ("$m", method), ("$t", t)) as string;
                if (sig != null) return SigReturnType(sig);
            }
        }
        return null;
    }

    /// <summary>Return type named in a method signature (attributes allowed), null for void / primitives.</summary>
    internal static string? SigReturnType(string sig)
    {
        var toks = XppLexer.Tokenize(sig, stopAtFirstBrace: true);
        var rt = CodeAnalyzer.ParseHeader(sig, toks).ReturnType;
        return rt == null || CodeAnalyzer.IsPrimitive(rt) ? null : rt;
    }

    /// <summary>"ret:A>b>c" → "A.b().c()" for display.</summary>
    internal static string ChainText(string via)
    {
        var parts = (via.StartsWith(CodeAnalyzer.ChainPrefix, StringComparison.Ordinal) ? via[CodeAnalyzer.ChainPrefix.Length..] : via).Split('>');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => $".{p}()"));
    }

    internal sealed record ChainRow(string Kind, string Via, int Line, string Type, string Obj, string? Method, string? Owner,
        string Path, string Tag, string? Resolved);

    /// <summary>Chained accesses to <paramref name="member"/> with their resolved receiver type.</summary>
    List<ChainRow> ChainedRefs(Store s, string member, string kinds, string? modelLike = null)
    {
        var rows = s.Query($"""
            SELECT r.kind, r.via, r.line, so.type, so.name, sm.name, sm.owner, f.path, md.package, md.name, md.tier
            FROM refs r JOIN objects so ON so.id = r.object_id JOIN files f ON f.id = r.file_id JOIN models md ON md.id = f.model_id
            LEFT JOIN methods sm ON sm.id = r.method_id
            WHERE r.target IS NULL AND r.member = $m AND r.kind IN ({kinds}) AND r.via LIKE 'ret:%'
              AND ($model IS NULL OR md.name LIKE $model ESCAPE '!' OR md.package LIKE $model ESCAPE '!')
            ORDER BY md.tier DESC, so.name, sm.name, r.line LIMIT 5000
            """, ("$m", member), ("$model", modelLike))
            .Select(r => (Kind: r.GetString(0), Via: r.GetString(1), Line: r.GetInt32(2), Type: r.GetString(3), Obj: r.GetString(4),
                Method: N(r, 5), Owner: N(r, 6), Path: r.GetString(7), Tag: Tag(r.GetString(8), r.GetString(9), r.GetInt32(10))))
            .ToList();
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        return rows.Select(r => new ChainRow(r.Kind, r.Via, r.Line, r.Type, r.Obj, r.Method, r.Owner, r.Path, r.Tag,
            ResolveType(s, r.Via[CodeAnalyzer.ChainPrefix.Length..], cache))).ToList();
    }
}

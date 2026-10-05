using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace XppGraft;

[McpServerToolType]
public static class McpTools
{
    public const string Instructions = """
        X++ (Dynamics 365 F&O) code index for PackagesLocalDirectory. Prefer these tools over Grep/Glob/Read on AOT XML:
        one call returns what would otherwise need reading several large XML files.
        - xpp_find: locate objects / methods / fields by name (wildcards * ?; "Obj.member" form).
        - xpp_object: skeleton of an object (fields, indexes, relations, methods with line ranges, extensions) — instead of reading the XML.
        - xpp_method: source of one method with file path and line range (use the range with Read/Edit when changing code).
        - xpp_callers / xpp_callees: who calls a method / what a method uses (resolved by declared variable types).
        - xpp_refs: every use of a class, table, field (name + member), EDT, enum, menu item or label (@File:Id).
        - xpp_extensions: CoC classes, table/form extensions, event handlers, derived classes of an object.
        - xpp_grep: regex over X++ method bodies (custom models; standard with model filter).
        - xpp_label: resolve @Label ids or search label text.
        Tiers: custom (non-Microsoft) models are fully indexed incl. code references; standard (Microsoft) models have
        objects, members, signatures, extensions and handlers but no code references.
        The index follows the files on disk (watcher + rescan), so results reflect Get Latest / local edits.
        Object XML files must still be edited through Read/Edit on the returned paths.
        """;

    static string Run(Func<string> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex) when (ex is not McpException)
        {
            Log.Warn(ex.ToString());
            throw new McpException($"xppgraft error: {ex.Message}");
        }
    }

    [McpServerTool(Name = "xpp_find", ReadOnly = true, Idempotent = true)]
    [Description("Find X++ objects, methods or fields by name. Examples: 'CustTable', 'Contoso*Invoice*', 'SalesLine.createLine', '*.validateWrite', 'CustTable.AccountNum'.")]
    public static string Find(Queries q,
        [Description("Name or pattern. 'Object.member' / 'Object::member' searches members; * and ? are wildcards; plain text = contains.")] string query,
        [Description("any (default) | object | method | field")] string? kind = null,
        [Description("Object type filter: class, table, form, edt, enum, view, entity, query, menuitem, tableext, formext, privilege, duty, role… or AxXxx")] string? type = null,
        [Description("Model or package name filter (wildcards allowed), e.g. Contoso")] string? model = null,
        [Description("Max rows per section (default 40)")] int limit = 40)
        => Run(() => q.Find(query, kind, type, model, limit));

    [McpServerTool(Name = "xpp_object", ReadOnly = true, Idempotent = true)]
    [Description("Skeleton of one AOT object parsed live from its XML: properties (labels resolved), class declaration, fields, indexes, relations, field groups, enum values, data sources, controls and menu elements as a tree (with full paths, ReferenceField/ReplacementFieldGroup, menu Parent/PositionType), methods with signatures and line ranges, plus extensions summary. Compiled-only packages return the skeleton rebuilt from compiler metadata. Large control trees are collapsed to 2 levels: drill down with parent/depth/filter.")]
    public static string Object(Queries q,
        [Description("Exact object name, e.g. CustTable, SalesFormLetter, ContosoFeatureBase, CustTable.Contoso")] string name,
        [Description("Optional type when the name is ambiguous: class, table, form, edt, enum, view, entity, query, menuitem…")] string? type = null,
        [Description("Optional comma list to limit output: props, declaration, fields, indexes, relations, fieldgroups, values, datasources, controls, menuitems, methods, extensions")] string? sections = null,
        [Description("Controls/menu tree: show only the subtree under this element name or path, e.g. TabPageGeneral or HeaderGrid")] string? parent = null,
        [Description("Controls/menu tree: number of levels to show (below parent if given)")] int depth = 0,
        [Description("Controls/menu tree: wildcard on element name or path, e.g. *Address*; prints full paths")] string? filter = null)
        => Run(() => q.Object(name, type, sections, parent, depth, filter));

    [McpServerTool(Name = "xpp_method", ReadOnly = true, Idempotent = true)]
    [Description("Source code of a method (or 'classDeclaration') with its XML file path and line range; also lists CoC wrappers and event handlers of that method. Form data source / control methods are matched by name too.")]
    public static string Method(Queries q,
        [Description("Object name, e.g. SalesTable")] string objectName,
        [Description("Method name, e.g. validateWrite")] string method,
        [Description("Optional object type filter")] string? type = null)
        => Run(() => q.Method(objectName, method, type));

    [McpServerTool(Name = "xpp_callers", ReadOnly = true, Idempotent = true)]
    [Description("Who calls Object.method (incl. calls on derived types, CoC 'next', handlers, methodStr references). depth>1 walks callers transitively. Also shows calls through base types and calls whose receiver type could not be resolved.")]
    public static string Callers(Queries q,
        [Description("Class/table/form name declaring the method")] string objectName,
        [Description("Method name")] string method,
        [Description("1-4, default 1")] int depth = 1,
        [Description("Max caller groups (default 80)")] int limit = 80)
        => Run(() => q.Callers(objectName, method, depth, limit));

    [McpServerTool(Name = "xpp_callees", ReadOnly = true, Idempotent = true)]
    [Description("What a method uses: calls, new, enum values, table fields, intrinsics (classStr/fieldNum…), types, labels, next/super. Full-tier (custom) models only.")]
    public static string Callees(Queries q,
        [Description("Object name")] string objectName,
        [Description("Method name")] string method,
        [Description("Optional object type filter")] string? type = null)
        => Run(() => q.Callees(objectName, method, type));

    [McpServerTool(Name = "xpp_refs", ReadOnly = true, Idempotent = true)]
    [Description("All references to an object or member: code (calls, types, fields, intrinsics, labels) and metadata (EDT/enum of fields, form data sources/controls, menu items, security entry points…). Use member for a table field or method, or a label id like @Contoso:MyLabel as name.")]
    public static string Refs(Queries q,
        [Description("Referenced object name (class, table, EDT, enum, menu item, form…) or label id @File:Key")] string name,
        [Description("Optional member: field or method name")] string? member = null,
        [Description("Optional kind: call, new, type, member, static, intrinsic, meta, label, coc, handler, extends, implements, extension")] string? kind = null,
        [Description("Optional model/package filter of the referencing code")] string? model = null,
        [Description("Max rows (default 150)")] int limit = 150)
        => Run(() => q.Refs(name, member, kind, model, limit));

    [McpServerTool(Name = "xpp_extensions", ReadOnly = true, Idempotent = true)]
    [Description("Everything that extends an object: CoC extension classes (with wrapped methods), table/form/enum/EDT extensions (with added fields), event handlers (SubscribesTo, DataEventHandler, FormEventHandler…), derived classes and interface implementations.")]
    public static string Extensions(Queries q,
        [Description("Object name, e.g. SalesTable, SalesLineType, CustInvoiceJour")] string name)
        => Run(() => q.Extensions(name));

    [McpServerTool(Name = "xpp_grep", ReadOnly = true, Idempotent = true)]
    [Description("Regex search (case-insensitive) inside X++ method bodies; hits grouped by object and method with file line numbers. Custom models by default; standard=true searches standard XML and requires model or object filter.")]
    public static string Grep(Queries q,
        [Description(".NET regex, e.g. 'ttsbegin', 'SalesTable::find\\\\(', 'while select.*InventTrans'")] string pattern,
        [Description("Optional model/package filter (wildcards)")] string? model = null,
        [Description("Optional object type filter")] string? type = null,
        [Description("Optional object name filter (wildcards)")] string? objectName = null,
        [Description("Search standard (Microsoft) models instead of custom ones")] bool standard = false,
        [Description("Max hits shown (default 80)")] int limit = 80)
        => Run(() => q.Grep(pattern, model, type, objectName, standard, limit));

    [McpServerTool(Name = "xpp_label", ReadOnly = true, Idempotent = true)]
    [Description("Resolve a label id (@SYS12345, @Contoso:Key) in all indexed languages, or search label texts to find an existing label id.")]
    public static string Label(Queries q,
        [Description("Label id starting with @, or text to search")] string query,
        [Description("Language for text search (default en-US; indexed: en-US, pl)")] string? lang = null,
        [Description("Max rows (default 30)")] int limit = 30)
        => Run(() => q.Label(query, lang, limit));

    [McpServerTool(Name = "xpp_status", ReadOnly = true)]
    [Description("Index status: indexed models per tier, counts, last refresh, background standard-tier indexing.")]
    public static string Status(IndexService svc) => Run(svc.StatusText);
}

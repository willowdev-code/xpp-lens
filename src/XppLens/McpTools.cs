using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace XppLens;

[McpServerToolType]
public static class McpTools
{
    public const string Instructions = """
        X++ (Dynamics 365 F&O) code index for PackagesLocalDirectory. Prefer these tools over Grep/Glob/Read on AOT XML:
        one call returns what would otherwise need reading several large XML files.
        - xpp_find: locate objects / methods / fields by name (wildcards * ?; "Obj.member" form; several names with ';').
        - xpp_object: skeleton of objects (fields, indexes, relations, controls, methods with line ranges, extensions).
        - xpp_method: method source with file path and line range; match/lines return only a numbered fragment.
        - xpp_callers / xpp_callees: who calls a method (custom and Microsoft code) / what a method uses.
        - xpp_refs: every use of a class, table, field, EDT, enum, menu item or label (@File:Id).
        - xpp_extensions: CoC classes, table/form extensions, event handlers, derived classes of an object.
        - xpp_scaffold: ready CoC wrapper / event handler / delegate subscriber with the exact signature.
        - xpp_build_errors: errors of the last Visual Studio build mapped to file lines.
        - xpp_security: menu item → privileges → duties → roles (and back).
        - xpp_join: relation path between two tables as a ready X++ join.
        - xpp_entity: data entity (data sources, field mapping, keys, staging) or the entities exposing a table.
        - xpp_changed: objects changed on disk since a time (after Get Latest / own edits).
        - xpp_grep: regex over X++ method bodies. xpp_label: resolve or search labels.
        Custom models have full code references; Microsoft models have objects, signatures, extensions, handlers and calls.
        The index follows the files on disk, so results reflect Get Latest / local edits.
        Object XML files must still be edited through Read/Edit on the returned paths.
        """;

    static string Run(string tool, object args, Func<string> f)
    {
        var sw = Stopwatch.StartNew();
        string? result = null;
        bool error = false;
        try
        {
            result = f();
            return result;
        }
        catch (Exception ex) when (ex is not McpException)
        {
            error = true;
            Log.Warn(ex.ToString());
            throw new McpException($"xpplens error: {ex.Message}");
        }
        catch
        {
            error = true;
            throw;
        }
        finally
        {
            Usage.Record(tool, args, result, sw.ElapsedMilliseconds, error);
        }
    }

    [McpServerTool(Name = "xpp_find", ReadOnly = true, Idempotent = true)]
    [Description("Find X++ objects, methods or fields by name. Examples: 'CustTable', 'Contoso*Invoice*', 'SalesLine.createLine', '*.validateWrite', 'CustTable.AccountNum'. Several queries in one call: 'CustTable; SalesTable.insert'.")]
    public static string Find(Queries q,
        [Description("Name or pattern. 'Object.member' / 'Object::member' searches members; * and ? are wildcards; plain text = contains; separate several queries with ';'.")] string query,
        [Description("any (default) | object | method | field")] string? kind = null,
        [Description("Object type filter: class, table, form, edt, enum, view, entity, query, menuitem, tableext, formext, privilege, duty, role… or AxXxx")] string? type = null,
        [Description("Model or package name filter (wildcards allowed), e.g. Contoso")] string? model = null,
        [Description("Max rows per section (default 40)")] int limit = 40)
        => Run("xpp_find", new { query, kind, type, model, limit }, () => q.Find(query, kind, type, model, limit));

    [McpServerTool(Name = "xpp_object", ReadOnly = true, Idempotent = true)]
    [Description("Skeleton of AOT objects parsed live from XML: properties (labels resolved), class declaration, fields, indexes, relations, field groups, enum values, data sources, controls and menu elements as a tree (full paths, ReferenceField/ReplacementFieldGroup, menu Parent/PositionType), methods with signatures and line ranges, extensions summary. Compiled-only packages: skeleton from compiler metadata. Large control trees collapse to 2 levels: drill down with parent/depth/filter. Several objects: separate names with ';'.")]
    public static string Object(Queries q,
        [Description("Exact object name(s), e.g. CustTable, ContosoFeatureBase, CustTable.Contoso; several with ';'")] string name,
        [Description("Optional type when the name is ambiguous: class, table, form, edt, enum, view, entity, query, menuitem…")] string? type = null,
        [Description("Optional comma list to limit output: props, declaration, fields, indexes, relations, fieldgroups, values, datasources, controls, menuitems, methods, extensions")] string? sections = null,
        [Description("Controls/menu tree: show only the subtree under this element name or path, e.g. TabPageGeneral or HeaderGrid")] string? parent = null,
        [Description("Controls/menu tree: number of levels to show (below parent if given)")] int depth = 0,
        [Description("Controls/menu tree: wildcard on element name or path, e.g. *Address*; prints full paths")] string? filter = null)
        => Run("xpp_object", new { name, type, sections, parent, depth, filter }, () => q.Object(name, type, sections, parent, depth, filter));

    [McpServerTool(Name = "xpp_method", ReadOnly = true, Idempotent = true)]
    [Description("Source of a method (or 'classDeclaration') with its XML file path and line range, plus CoC wrappers and event handlers of it. For long methods pass match (regex) and/or lines to get only a numbered fragment — the signature and variable declarations are always included. Several methods: method='a;b', or objectName='SalesTable.insert;CustTable::find' without method.")]
    public static string Method(Queries q,
        [Description("Object name, e.g. SalesTable — or 'Object.method' items separated by ';' when method is omitted")] string objectName,
        [Description("Method name(s), e.g. validateWrite or 'insert;update'")] string? method = null,
        [Description("Optional object type filter")] string? type = null,
        [Description("Only lines matching this regex (case-insensitive), with context")] string? match = null,
        [Description("Only this file line range, e.g. 120-180 (numbers as in the returned path:start-end)")] string? lines = null,
        [Description("Context lines around each match (default 3)")] int context = 3)
        => Run("xpp_method", new { objectName, method, type, match, lines, context }, () => q.Method(objectName, method, type, match, lines, context));

    [McpServerTool(Name = "xpp_callers", ReadOnly = true, Idempotent = true)]
    [Description("Who calls Object.method — custom code first, then compiled packages and Microsoft code: calls on typed variables and derived types, chained calls like Table::find().m(), CoC 'next', handlers, methodStr. depth>1 walks callers transitively.")]
    public static string Callers(Queries q,
        [Description("Class/table/form name declaring the method")] string objectName,
        [Description("Method name")] string method,
        [Description("1-4, default 1")] int depth = 1,
        [Description("Max caller groups (default 80)")] int limit = 80,
        [Description("false = leave callers inside Microsoft code out")] bool standard = true)
        => Run("xpp_callers", new { objectName, method, depth, limit, standard }, () => q.Callers(objectName, method, depth, limit, standard));

    [McpServerTool(Name = "xpp_callees", ReadOnly = true, Idempotent = true)]
    [Description("What a method uses: calls (chained receivers typed from return values), new, enum values, table fields, intrinsics (classStr/fieldNum…), types, labels, next/super. Custom models and compiled packages.")]
    public static string Callees(Queries q,
        [Description("Object name")] string objectName,
        [Description("Method name")] string method,
        [Description("Optional object type filter")] string? type = null)
        => Run("xpp_callees", new { objectName, method, type }, () => q.Callees(objectName, method, type));

    [McpServerTool(Name = "xpp_refs", ReadOnly = true, Idempotent = true)]
    [Description("All references to an object or member: code (calls, types, fields — also on chained receivers, intrinsics, labels) and metadata (EDT/enum of fields, form data sources/controls, menu items, security entry points…). Use member for a table field or method, or a label id like @Contoso:MyLabel as name.")]
    public static string Refs(Queries q,
        [Description("Referenced object name (class, table, EDT, enum, menu item, form…) or label id @File:Key")] string name,
        [Description("Optional member: field or method name")] string? member = null,
        [Description("Optional kind: call, new, type, member, static, intrinsic, meta, label, coc, handler, extends, implements, extension")] string? kind = null,
        [Description("Optional model/package filter of the referencing code")] string? model = null,
        [Description("Max rows (default 150)")] int limit = 150)
        => Run("xpp_refs", new { name, member, kind, model, limit }, () => q.Refs(name, member, kind, model, limit));

    [McpServerTool(Name = "xpp_extensions", ReadOnly = true, Idempotent = true)]
    [Description("Everything that extends an object: CoC extension classes (with wrapped methods), table/form/enum/EDT extensions (with added fields), event handlers (SubscribesTo, DataEventHandler, FormEventHandler…), derived classes and interface implementations.")]
    public static string Extensions(Queries q,
        [Description("Object name, e.g. SalesTable, SalesLineType, CustInvoiceJour")] string name)
        => Run("xpp_extensions", new { name }, () => q.Extensions(name));

    [McpServerTool(Name = "xpp_scaffold", ReadOnly = true, Idempotent = true)]
    [Description("Ready-to-paste X++ with the exact signature from the index: kind=coc (Chain of Command wrapper, default values removed, warnings for private/final/non-wrappable), event (table/form/data source/control/field event handler, parameters learned from existing handlers), delegate (SubscribesTo subscriber), pre/post (legacy handlers). Class names follow the naming pattern found in your models.")]
    public static string Scaffold(Queries q,
        [Description("coc | event | delegate | pre | post")] string kind,
        [Description("Class, table, form, view or data entity name")] string objectName,
        [Description("Method name (coc/pre/post), event name e.g. Inserted / ValidatedWrite / Initialized / Clicked (event), or delegate name")] string member,
        [Description("Forms: data source name, 'DataSource.Field' or control name")] string? element = null,
        [Description("Optional class name instead of the suggested one")] string? className = null,
        [Description("Optional object type when a form and a table share the name: table, form, class…")] string? type = null)
        => Run("xpp_scaffold", new { kind, objectName, member, element, className, type }, () => q.Scaffold(kind, objectName, member, element, className, type));

    [McpServerTool(Name = "xpp_build_errors", ReadOnly = true)]
    [Description("Errors (or warnings) of the last X++ build in Visual Studio, read from each package's BuildModelResult.xml and mapped to the XML file line of the method; flags files changed after that build.")]
    public static string BuildErrors(Queries q,
        [Description("Model or package filter (wildcards); default: your custom models")] string? model = null,
        [Description("error (default) | warning (errors + warnings) | all")] string? severity = null,
        [Description("Max diagnostics (default 50)")] int limit = 50)
        => Run("xpp_build_errors", new { model, severity, limit }, () => q.BuildErrors(model, severity, limit));

    [McpServerTool(Name = "xpp_security", ReadOnly = true, Idempotent = true)]
    [Description("Security chain. Menu item (or other entry point) / form → privileges (with granted access) → duties → roles. Privilege → entry points + duties/roles. Duty → privileges + roles. Role → duties, privileges, sub-roles.")]
    public static string Security(Queries q,
        [Description("Menu item, form, privilege, duty or role name")] string name,
        [Description("Optional type: display, action, output, form, privilege, duty, role")] string? type = null,
        [Description("Max rows per list (default 40)")] int limit = 40)
        => Run("xpp_security", new { name, type, limit }, () => q.Security(name, type, limit));

    [McpServerTool(Name = "xpp_join", ReadOnly = true, Idempotent = true)]
    [Description("Shortest relation path between two tables (table relations in both directions) with the field pairs, returned as a ready X++ select/join.")]
    public static string Join(Queries q,
        [Description("Table to start from, e.g. CustInvoiceJour")] string from,
        [Description("Table to reach, e.g. CustTable")] string to,
        [Description("Max hops (default 3, max 5)")] int maxHops = 3,
        [Description("Paths to show (default 3)")] int limit = 3)
        => Run("xpp_join", new { from, to, maxHops, limit }, () => q.Join(from, to, maxHops, limit));

    [McpServerTool(Name = "xpp_entity", ReadOnly = true, Idempotent = true)]
    [Description("Data entity: public names, staging table, data source tree with joins, field mapping (field ← DataSource.Field), keys, methods, extensions. Accepts the entity, its public entity/collection name, or a table/view (then lists entities that use it).")]
    public static string Entity(Queries q,
        [Description("Entity name, public entity name, or table name")] string name,
        [Description("Optional comma list: props, datasources, keys, fields, methods, extensions")] string? sections = null,
        [Description("Max fields / entities listed (default 200)")] int limit = 200)
        => Run("xpp_entity", new { name, sections, limit }, () => q.Entity(name, sections, limit));

    [McpServerTool(Name = "xpp_changed", ReadOnly = true)]
    [Description("Objects whose XML changed on disk since a time — e.g. what a Get Latest brought in, or your own recent edits. Grouped counts per model plus the newest objects.")]
    public static string Changed(Queries q,
        [Description("24h (default), 3d, 90m, 2026-10-01 or '2026-10-01 14:00'")] string? since = null,
        [Description("Model or package filter (wildcards)")] string? model = null,
        [Description("Object type filter")] string? type = null,
        [Description("Max objects listed (default 100)")] int limit = 100)
        => Run("xpp_changed", new { since, model, type, limit }, () => q.Changed(since, model, type, limit));

    [McpServerTool(Name = "xpp_grep", ReadOnly = true, Idempotent = true)]
    [Description("Regex search (case-insensitive) inside X++ method bodies; hits grouped by object and method with file line numbers. Custom models by default; standard=true searches standard XML and requires model or object filter.")]
    public static string Grep(Queries q,
        [Description(".NET regex, e.g. 'ttsbegin', 'SalesTable::find\\\\(', 'while select.*InventTrans'")] string pattern,
        [Description("Optional model/package filter (wildcards)")] string? model = null,
        [Description("Optional object type filter")] string? type = null,
        [Description("Optional object name filter (wildcards)")] string? objectName = null,
        [Description("Search standard (Microsoft) models instead of custom ones")] bool standard = false,
        [Description("Max hits shown (default 80)")] int limit = 80)
        => Run("xpp_grep", new { pattern, model, type, objectName, standard, limit }, () => q.Grep(pattern, model, type, objectName, standard, limit));

    [McpServerTool(Name = "xpp_label", ReadOnly = true, Idempotent = true)]
    [Description("Resolve a label id (@SYS12345, @Contoso:Key) in all indexed languages, or search label texts to find an existing label id.")]
    public static string Label(Queries q,
        [Description("Label id starting with @, or text to search")] string query,
        [Description("Language for text search (default: display language)")] string? lang = null,
        [Description("Max rows (default 30)")] int limit = 30)
        => Run("xpp_label", new { query, lang, limit }, () => q.Label(query, lang, limit));

    [McpServerTool(Name = "xpp_status", ReadOnly = true)]
    [Description("Index status: indexed models per tier, counts, last refresh, background standard-tier indexing.")]
    public static string Status(IndexService svc) => Run("xpp_status", new { }, svc.StatusText);
}

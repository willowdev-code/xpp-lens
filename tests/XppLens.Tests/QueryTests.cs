namespace XppLens.Tests;

/// <summary>End to end: XML fixtures → index → the answers the MCP tools return.</summary>
[Collection("index")]
public class QueryTests(IndexFixture fx)
{
    Queries Q => fx.Q;

    [Fact]
    public void Catalog_puts_publishers_in_tiers()
    {
        var status = fx.Service.StatusText();
        Assert.Contains("full tier: 1 models", status);
        Assert.Contains("standard tier: 1 models", status);
        Assert.Contains("full-tier models: ContosoCore", status);
    }

    [Fact]
    public void Find_treats_underscore_literally()
    {
        var res = Q.Find("calc_T*", "method", null, null, 40);
        Assert.Contains("ContosoHelper.calc_Total", res);
        Assert.DoesNotContain("calcXTotal", res);
    }

    [Fact]
    public void Find_extension_by_dotted_name()
    {
        var res = Q.Find("*Customer.Contoso", null, "tableext", null, 40);
        Assert.Contains("AxTableExtension DemoCustomer.Contoso", res);
    }

    [Fact]
    public void Find_batch_returns_a_section_per_query()
    {
        var res = Q.Find("DemoCustomer; ContosoHelper.customer", null, null, null, 5);
        Assert.Contains("### DemoCustomer", res);
        Assert.Contains("### ContosoHelper.customer", res);
        Assert.Contains("AxClass ContosoHelper.customer", res);
    }

    [Fact]
    public void Object_skeleton_and_batch()
    {
        var res = Q.Object("DemoCustomer; DemoInvoice", "table", null);
        Assert.Contains("### DemoCustomer", res);
        Assert.Contains("AccountNum: String edt=DemoAccount mandatory", res);
        Assert.Contains("AccountIdx: (AccountNum) unique AK", res);
        Assert.Contains("DemoCustomer: DemoCustomer card=ZeroMore relCard=ExactlyOne on InvoiceAccount=AccountNum", res);
        Assert.Contains("DemoCustomer_Contoso_Extension", res);
    }

    [Fact]
    public void Method_full_fragment_and_batch()
    {
        var full = Q.Method("ContosoInvoiceService", "run", null);
        Assert.Contains("ContosoInvoiceService.xml:", full);
        Assert.Contains("ttsbegin;", full);
        Assert.DoesNotContain("fragment:", full);

        var frag = Q.Method("ContosoInvoiceService", "run", null, match: "\\.update\\(", context: 0);
        Assert.Contains("fragment: 1 line(s) matching", frag);
        Assert.Contains("customer.update();", frag);
        Assert.Contains("DemoCustomer customer = DemoCustomer::find('C0001');", frag);
        Assert.DoesNotContain("info(strFmt", frag);

        var batch = Q.Method("ContosoHelper.calc_Total; DemoCustomer::find", null, null);
        Assert.Contains("### ContosoHelper.calc_Total", batch);
        Assert.Contains("### DemoCustomer.find", batch);
        Assert.Contains("select firstonly customer", batch);
    }

    [Fact]
    public void Callers_cover_typed_chained_extension_and_standard_code()
    {
        var res = Q.Callers("DemoCustomer", "creditMax", 1, 80);
        Assert.Contains("ContosoInvoiceService.run", res);
        Assert.Contains("ContosoHelper.calc_Total", res);
        Assert.Contains("DemoCustomer_Contoso_Extension.validateWrite", res);
        Assert.Contains("(chained)", res);
        Assert.Contains("DemoPosting.post [StdBase, std]", res);
        Assert.Contains("in standard code", res);

        var noStd = Q.Callers("DemoCustomer", "creditMax", 1, 80, standard: false);
        Assert.DoesNotContain("DemoPosting.post", noStd);
    }

    [Fact]
    public void Chained_call_inside_standard_code_is_found()
    {
        var res = Q.Callers("DemoCustomer", "name", 1, 80);
        Assert.Contains("DemoPosting.post [StdBase, std]", res);
        Assert.Contains("ContosoInvoiceService.run", res);
    }

    [Fact]
    public void Refs_include_members_on_chained_receivers()
    {
        var res = Q.Refs("DemoCustomer", "creditMax", null, null, 150);
        Assert.Contains("on chained receivers", res);
        Assert.Contains("DemoCustomer.find().creditMax", res);
    }

    [Fact]
    public void Callees_show_resolved_chains()
    {
        var res = Q.Callees("ContosoInvoiceService", "run", null);
        Assert.Contains("DemoCustomer::find", res);
        Assert.Contains("DemoCustomer.find().name (= DemoCustomer.name)", res);
        Assert.Contains("@DemoLabels:CreditLimit", res);
    }

    [Fact]
    public void Extensions_list_coc_table_extension_and_handlers()
    {
        var res = Q.Extensions("DemoCustomer");
        Assert.Contains("DemoCustomer_Contoso_Extension", res);
        Assert.Contains("wraps validateWrite", res);
        Assert.Contains("AxTableExtension DemoCustomer.Contoso", res);
        Assert.Contains("ContosoSegment_Code", res);
        Assert.Contains("ContosoEventHandlers.DemoCustomer_onInserted", res);
    }

    [Fact]
    public void Scaffold_coc_wrapper_has_exact_signature_without_defaults()
    {
        var res = Q.Scaffold("coc", "DemoCustomer", "find", null, null, "table");
        Assert.Contains("[ExtensionOf(tableStr(DemoCustomer))]", res);
        Assert.Contains("final class DemoCustomer_Contoso_Extension", res);
        Assert.Contains("public static DemoCustomer find(DemoAccount _account, boolean _forUpdate)", res);
        Assert.Contains("DemoCustomer ret = next find(_account, _forUpdate);", res);
        Assert.Contains("existing extension classes of DemoCustomer", res);
    }

    [Fact]
    public void Scaffold_coc_on_form_data_source()
    {
        var res = Q.Scaffold("coc", "DemoCustomer", "validateWrite", "DemoCustomer", null);
        Assert.Contains("[ExtensionOf(formDataSourceStr(DemoCustomer, DemoCustomer))]", res);
        Assert.Contains("boolean ret = next validateWrite();", res);
    }

    [Fact]
    public void Scaffold_event_learns_parameters_and_rejects_unknown_events()
    {
        var res = Q.Scaffold("event", "DemoCustomer", "onValidatedWrite", null, null, "table");
        Assert.Contains("[DataEventHandler(tableStr(DemoCustomer), DataEventType::ValidatedWrite)]", res);
        Assert.Contains("public static void DemoCustomer_onValidatedWrite(Common sender, DataEventArgs e)", res);

        var bad = Q.Scaffold("event", "DemoCustomer", "Exploded", null, null, "table");
        Assert.Contains("Known:", bad);
        Assert.Contains("Inserted", bad);
    }

    [Fact]
    public void Scaffold_delegate_subscriber()
    {
        var res = Q.Scaffold("delegate", "DemoPosting", "posted", null, null);
        Assert.Contains("[SubscribesTo(classStr(DemoPosting), delegateStr(DemoPosting, posted))]", res);
        Assert.Contains("public static void DemoPosting_posted(DemoCustomer _customer)", res);
    }

    [Fact]
    public void Security_chain_from_menu_item_to_roles()
    {
        var res = Q.Security("DemoCustomer", "display", 40);
        Assert.Contains("privilege DemoCustomerMaintain", res);
        Assert.Contains("grant=Delete,Read,Update", res);
        Assert.Contains("duty DemoCustomerDuty", res);
        Assert.Contains("roles: DemoClerk", res);

        var role = Q.Security("DemoClerk", "role", 40);
        Assert.Contains("DemoCustomerDuty: 1 privilege(s)", role);

        var form = Q.Security("DemoCustomer", "form", 40);
        Assert.Contains("is opened by 1 menu item(s)", form);
    }

    [Fact]
    public void Join_path_over_two_relations()
    {
        var res = Q.Join("DemoInvoiceLine", "DemoCustomer", 3, 3);
        Assert.Contains("DemoInvoiceLine → DemoCustomer: 2 hop(s)", res);
        Assert.Contains("where demoInvoice.InvoiceId == demoInvoiceLine.InvoiceId", res);
        Assert.Contains("where demoCustomer.AccountNum == demoInvoice.InvoiceAccount", res);

        var back = Q.Join("DemoCustomer", "DemoInvoice", 3, 3);
        Assert.Contains("reverse", back);
        Assert.Contains("where demoInvoice.InvoiceAccount == demoCustomer.AccountNum", back);
    }

    [Fact]
    public void Entity_by_name_public_name_and_table()
    {
        var res = Q.Entity("DemoCustomerEntity", null, 200);
        Assert.Contains("PublicEntityName=DemoCustomer", res);
        Assert.Contains("CustomerAccount ← DemoCustomer.AccountNum", res);
        Assert.Contains("DemoInvoice: DemoInvoice joinMode=OuterJoin on InvoiceAccount=DemoCustomer.AccountNum", res);
        Assert.Contains("EntityKey (CustomerAccount)", res);
        Assert.Contains("staging table: DemoCustomerStaging", res);

        Assert.Contains("AxDataEntityView DemoCustomerEntity", Q.Entity("DemoCustomers", null, 200));
        Assert.Contains("DemoCustomerEntity [StdBase, std] — data source DemoInvoice", Q.Entity("DemoInvoice", null, 200));
    }

    [Fact]
    public void Changed_lists_recent_files_only()
    {
        var res = Q.Changed("1h", null, null, 100);
        Assert.Contains("AxClass ContosoHelper", res);
        Assert.DoesNotContain("ContosoInvoiceService", res);
        Assert.StartsWith("nothing changed", Q.Changed("2026-01-01", "StdBase", null, 100).Replace("\r", ""));
    }

    [Fact]
    public void Build_errors_map_to_file_lines()
    {
        var res = Q.BuildErrors(null, null, 50);
        Assert.Contains("ContosoCore — built", res);
        Assert.Contains("1 error, 1 warning", res);
        Assert.Contains("Error AxClass ContosoInvoiceService.run L8:9 → ", res);
        Assert.Contains("[file changed after this build]", res);
        Assert.DoesNotContain("BPUnusedMethod", res);
        Assert.Contains("BPUnusedMethod", Q.BuildErrors(null, "warning", 50));
    }

    [Fact]
    public void Labels_resolve_and_search()
    {
        Assert.Contains("en-US: Credit limit", Q.Label("@DemoLabels:CreditLimit", null, 30));
        Assert.Contains("@DemoLabels:Customer", Q.Label("Customer", null, 30));
    }
}

namespace XppLens.Tests;

/// <summary>Lexer, header parsing and reference extraction on X++ snippets (no index needed).</summary>
public class AnalyzerTests
{
    static List<CodeRef> RefsOf(string code, string thisType = "ContosoInvoiceService")
    {
        var toks = XppLexer.Tokenize(code);
        var h = CodeAnalyzer.ParseHeader(code, toks);
        var refs = new List<CodeRef>();
        var locals = CodeAnalyzer.Declarations(toks, h.BodyTokenStart, refs, 0);
        var ctx = new CodeContext { ObjectType = "AxClass", ObjectName = thisType, ThisType = thisType, MethodName = h.MethodName };
        CodeAnalyzer.BodyRefs(ctx, toks, h.BodyTokenStart, 0, refs, locals);
        return refs;
    }

    [Fact]
    public void Lexer_drops_comments_and_keeps_strings_and_scope_operator()
    {
        var toks = XppLexer.Tokenize("// note\nx = A::b('it''s', \"q\"); /* block\n */ y++;");
        Assert.DoesNotContain(toks, t => t.Text.Contains("note") || t.Text.Contains("block"));
        Assert.Contains(toks, t => t.Kind == TokKind.Punct && t.Text == "::");
        Assert.Contains(toks, t => t.Kind == TokKind.String && t.Text == "q");
        Assert.Equal(2, toks.First(t => t.Text == "y").Line);
    }

    [Fact]
    public void Header_reads_attributes_modifiers_and_return_type()
    {
        const string code = "[SysObsolete('x')]\npublic static DemoCustomer find(DemoAccount _a, boolean _f = false)\n{\n}";
        var h = CodeAnalyzer.ParseHeader(code, XppLexer.Tokenize(code));
        Assert.Equal("find", h.MethodName);
        Assert.Equal("DemoCustomer", h.ReturnType);
        Assert.True(h.IsStatic);
        Assert.Single(h.Attributes);
    }

    [Fact]
    public void Typed_static_and_new_calls_are_resolved()
    {
        var refs = RefsOf("""
            public void run()
            {
                DemoCustomer customer = DemoCustomer::find('C1');
                var helper = new ContosoHelper();
                customer.creditMax();
                helper.calc_Total();
            }
            """);
        Assert.Contains(refs, r => r is { Kind: "call", Target: "DemoCustomer", Member: "find", Via: "::" });
        Assert.Contains(refs, r => r is { Kind: "call", Target: "DemoCustomer", Member: "creditMax" });
        Assert.Contains(refs, r => r is { Kind: "call", Target: "ContosoHelper", Member: "calc_Total" });
        Assert.Contains(refs, r => r is { Kind: "new", Target: "ContosoHelper" });
    }

    [Fact]
    public void Chained_calls_record_the_receiver_chain()
    {
        var refs = RefsOf("""
            public void run()
            {
                str n = DemoCustomer::find('C1').name();
                real x = new ContosoHelper().customer().creditMax();
                var c = DemoCustomer::find('C2');
                c.validateWrite();
                real y = DemoCustomer::find('C3').CreditMax;
            }
            """);
        Assert.Contains(refs, r => r is { Kind: "call", Target: null, Member: "name", Via: "ret:DemoCustomer>find" });
        Assert.Contains(refs, r => r is { Kind: "call", Target: "ContosoHelper", Member: "customer" });
        Assert.Contains(refs, r => r is { Kind: "call", Target: null, Member: "creditMax", Via: "ret:ContosoHelper>customer" });
        Assert.Contains(refs, r => r is { Kind: "call", Target: null, Member: "validateWrite", Via: "ret:DemoCustomer>find" });
        Assert.Contains(refs, r => r is { Kind: "member", Target: null, Member: "CreditMax", Via: "ret:DemoCustomer>find" });
    }

    [Fact]
    public void Unknown_receivers_stay_unresolved()
    {
        var refs = RefsOf("public void run()\n{\n    something.doIt();\n}");
        Assert.Contains(refs, r => r is { Kind: "call", Target: null, Member: "doIt", Via: null });
    }

    [Fact]
    public void Signature_parts_drop_default_values()
    {
        var sp = Queries.ParseSignature("[Hookable(true)] public static DemoCustomer find(DemoAccount _account, boolean _forUpdate = false)");
        Assert.NotNull(sp);
        Assert.Equal("DemoCustomer", sp.ReturnType);
        Assert.Equal(["public", "static"], sp.Modifiers);
        Assert.Equal([("DemoAccount", "_account"), ("boolean", "_forUpdate")], sp.Params);
    }

    [Fact]
    public void Return_type_of_signature()
    {
        Assert.Equal("DemoCustomer", Queries.SigReturnType("public static DemoCustomer find(DemoAccount _a)"));
        Assert.Null(Queries.SigReturnType("public void run()"));
        Assert.Null(Queries.SigReturnType("public real creditMax()"));
    }

    [Fact]
    public void Chain_text_for_display()
    {
        Assert.Equal("DemoCustomer.find().name()", Queries.ChainText("ret:DemoCustomer>find>name"));
    }
}

/// <summary>Small pure helpers: fragments, relation info, dates, build paths, usage log.</summary>
public class HelperTests
{
    static readonly List<(int No, string Text)> Method =
    [
        (10, "    public void run()"), (11, "    {"), (12, "        DemoCustomer customer;"), (13, "        int i;"),
        (14, "        i = 1;"), (15, "        i = 2;"), (16, "        ttsbegin;"), (17, "        customer.update();"),
        (18, "        ttscommit;"), (19, "        i = 3;"), (20, "        i = 4;"), (21, "    }"),
    ];

    [Fact]
    public void Fragment_keeps_signature_declarations_and_matches_with_context()
    {
        var text = CodeFragment.Render(Method, FragmentSpec.Create("update", null, 1)!);
        Assert.Contains("   10:     public void run()", text);
        Assert.Contains("   12:         DemoCustomer customer;", text);
        Assert.Contains("   16:         ttsbegin;", text);
        Assert.Contains("   17:         customer.update();", text);
        Assert.Contains("   18:         ttscommit;", text);
        Assert.DoesNotContain("i = 1;", text);
        Assert.Contains("… 2 line(s)", text);
        Assert.Contains("1 line(s) matching 'update'", text);
    }

    [Fact]
    public void Fragment_by_line_range()
    {
        var text = CodeFragment.Render(Method, FragmentSpec.Create(null, "19-20", 0)!);
        Assert.Contains("   19:         i = 3;", text);
        Assert.Contains("   20:         i = 4;", text);
        Assert.DoesNotContain("ttsbegin", text);
    }

    [Fact]
    public void No_fragment_spec_without_match_or_lines()
    {
        Assert.Null(FragmentSpec.Create(null, null, 3));
        Assert.Null(FragmentSpec.Create(" ", "", 3));
    }

    [Fact]
    public void Relation_info_is_split_into_table_pairs_and_fixed_constraints()
    {
        var (related, pairs, fixedC) = Queries.ParseRelationInfo("DemoCustomer card=ZeroMore relCard=ExactlyOne on InvoiceAccount=AccountNum, Module");
        Assert.Equal("DemoCustomer", related);
        Assert.Equal([("InvoiceAccount", "AccountNum")], pairs);
        Assert.Equal(["Module"], fixedC);
    }

    [Fact]
    public void Since_accepts_durations_and_dates()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddHours(-24), Queries.ParseSince(null, now));
        Assert.Equal(now.AddDays(-3), Queries.ParseSince("3d", now));
        Assert.Equal(now.AddMinutes(-90), Queries.ParseSince("90m", now));
        Assert.NotNull(Queries.ParseSince("2026-10-01", now));
        Assert.Null(Queries.ParseSince("yesterday-ish", now));
    }

    [Fact]
    public void Diagnostic_paths_map_to_objects_methods_and_elements()
    {
        Assert.Equal(new Queries.DiagTarget("AxClass", "Foo", null, "bar", null), Queries.ParseDiagnosticPath("dynamics://Class/Foo/Method/bar"));
        Assert.Equal(new Queries.DiagTarget("AxForm", "Foo", "DataSource:Bar", "init", "Bar"),
            Queries.ParseDiagnosticPath("dynamics://Form/Foo/DataSource/Bar/Method/init"));
        Assert.Equal(new Queries.DiagTarget("AxForm", "Foo", "DataSource:Bar/Field:Qty", "validate", "Qty"),
            Queries.ParseDiagnosticPath("dynamics://Form/Foo/DataSource/Bar/DataField/Qty/Method/validate"));
        Assert.Equal(new Queries.DiagTarget("AxForm", "Foo", "Control:OkButton", "clicked", "OkButton"),
            Queries.ParseDiagnosticPath("dynamics://Form/Foo/FormDesign/AxFormDesign/FormButtonGroupControl/Group/FormButtonControl/OkButton/Method/clicked"));
        Assert.Equal(new Queries.DiagTarget("AxFormExtension", "Foo.Contoso", null, null, "Design/Controls/Tab/Page"),
            Queries.ParseDiagnosticPath("AxFormExtension/Foo.Contoso/Design/Controls/Tab/Page"));
        Assert.Equal(new Queries.DiagTarget("AxView", "Foo", null, null, null), Queries.ParseDiagnosticPath("dynamics://View/Foo"));
        Assert.Null(Queries.ParseDiagnosticPath(@"C:\file.xml").Name);
    }

    [Fact]
    public void Compiler_lines_count_through_declaration_and_methods()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PackagesLocalDirectory", "ContosoCore", "ContosoCore", "AxClass", "ContosoHelper.xml");
        var po = XmlObjectParser.Parse(file, ParseMode.Render);
        // Declaration: file lines 6-8 ("public class ContosoHelper", "{", member); the closing brace is not counted.
        Assert.Equal((6, "classDeclaration"), Queries.MapCodeLine(po, 1));
        Assert.Equal((8, "classDeclaration"), Queries.MapCodeLine(po, 3));
        // calc_Total: 5 lines (signature, {, return, }, blank) from file line 15.
        Assert.Equal((15, "calc_Total"), Queries.MapCodeLine(po, 4));
        Assert.Equal((17, "calc_Total"), Queries.MapCodeLine(po, 6));
        // calcXTotal follows: the 9th line of the code is its signature in file line 25.
        Assert.Equal((25, "calcXTotal"), Queries.MapCodeLine(po, 9));
        Assert.Null(Queries.MapCodeLine(po, 500));
    }

    [Fact]
    public void Batch_lists_are_split_and_deduplicated()
    {
        Assert.Equal(["A", "B.c", "D"], Queries.SplitList("A; B.c;\nD; a"));
        Assert.Empty(Queries.SplitList("  "));
    }

    [Fact]
    public void Empty_answers_are_recognised()
    {
        Assert.True(Usage.LooksEmpty("nothing found"));
        Assert.True(Usage.LooksEmpty("[index refreshed: 2 changed file(s)]\nNo object named 'X'."));
        Assert.True(Usage.LooksEmpty("'DemoCustomer' has no method 'foo'. Similar: find"));
        Assert.True(Usage.LooksEmpty("labels (en-US) matching 'zzz': 0"));
        Assert.False(Usage.LooksEmpty("objects (1):\n  AxTable DemoCustomer"));
    }

    [Fact]
    public void Usage_log_round_trip_and_report()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xpplens-usage-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("XPPLENS_USAGE_DIR", dir);
        var enabled = Usage.Enabled;
        try
        {
            Usage.Enabled = true;
            Usage.Record("xpp_find", new { query = "DemoCustomer", limit = 40 }, new string('x', 400), 12, false);
            Usage.Record("xpp_find", new { query = "Nope" }, "nothing found", 3, false);
            Usage.Record("xpp_method", new { objectName = "A" }, null, 5, true);
            var all = Usage.Load(DateTime.UtcNow.AddMinutes(-5));
            Assert.Equal(3, all.Count);
            Assert.Equal(100, all[0].Tokens);
            var report = Usage.Report(1, 5);
            Assert.Contains("xpp_find", report);
            Assert.Contains("empty answers", report);
            Assert.Contains("\"query\":\"Nope\"", report);
            Assert.Contains("errors", report);
        }
        finally
        {
            Usage.Enabled = enabled;
            Environment.SetEnvironmentVariable("XPPLENS_USAGE_DIR", null);
            Directory.Delete(dir, true);
        }
    }
}

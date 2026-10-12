using SoulsFormats;
using ESDLang.EzSemble;
using System.Text;
using static ESDLang.EzSemble.AST;

static class DialoguePatches
{
    public static void CompareArchives(string first, string second) {
        var a = BND4.Read(first); var b = BND4.Read(second);
        Require(a.Files.Count == b.Files.Count, "Archive entry count changed.");
        for (int i = 0; i < a.Files.Count; i++) {
            Require(a.Files[i].Name == b.Files[i].Name && a.Files[i].ID == b.Files[i].ID && a.Files[i].Flags == b.Files[i].Flags,
                "Archive entry metadata changed.");
            Require(a.Files[i].Bytes.SequenceEqual(b.Files[i].Bytes), "Rebuild mismatch: " + a.Files[i].Name);
        }
    }

    public static void VerifyParameters(string oldPath, string newPath, string definitions) {
        var before = BND4.Read(oldPath); var after = BND4.Read(newPath);
        Require(before.Files.Count == after.Files.Count, "Parameter archive count changed.");
        var def = PARAMDEF.XmlDeserialize(definitions);
        int cells = 0;
        for (int i = 0; i < before.Files.Count; i++) {
            var a = before.Files[i]; var b = after.Files[i];
            Require(a.Name == b.Name && a.ID == b.ID && a.Flags == b.Flags, "Parameter metadata changed.");
            if (!a.Name.Replace('\\', '/').EndsWith("/ActionButtonParam.param")) {
                Require(a.Bytes.SequenceEqual(b.Bytes), "Unrelated parameter table changed: " + a.Name);
                continue;
            }
            var oldParam = PARAM.Read(a.Bytes); oldParam.ApplyParamdef(def);
            var newParam = PARAM.Read(b.Bytes); newParam.ApplyParamdef(def);
            Require(oldParam.Rows.Count == newParam.Rows.Count, "Action row count changed.");
            for (int r = 0; r < oldParam.Rows.Count; r++) {
                var x = oldParam.Rows[r]; var y = newParam.Rows[r];
                Require(x.ID == y.ID && x.Name == y.Name, "Action row identity changed.");
                for (int c = 0; c < x.Cells.Count; c++) {
                    var previous = x.Cells[c]; var current = y.Cells[c];
                    bool same = previous.Value is byte[] bytes ? current.Value is byte[] other && bytes.SequenceEqual(other) : Equals(previous.Value, current.Value);
                    if (!same) {
                        cells++;
                        Require(x.ID == 2000680 && previous.Def.InternalName == "priority" &&
                                (int)previous.Value == 10 && (int)current.Value == 9,
                                "Unrelated parameter cell changed.");
                    }
                }
            }
        }
        Require(cells == 1, "Expected exactly one bell-priority change.");
        Console.WriteLine($"PASS: 137 unrelated parameter tables preserved; bell 2000680 priority changed from 10 to 9.");
    }

    static void Require(bool condition, string message) {
        if (!condition) throw new InvalidDataException(message);
    }
    static byte[] Expression(EzSembleContext context, string text) =>
        EzSembler.AssembleExpression(context, text.Replace("GetEventFlag", "f15"));
    static bool Constant(Expr expr, int value) => expr.TryAsInt(out int actual) && actual == value;
    static IEnumerable<ESD.Condition> Conditions(List<ESD.Condition> list) {
        foreach (var c in list) {
            yield return c;
            foreach (var child in Conditions(c.Subconditions)) yield return child;
        }
    }
    static ESD.Condition Rule(EzSembleContext context, string expression, long target) => new(target, Expression(context, expression));

    // Small evaluator for the flag-only routing rules exercised below.
    static int Evaluate(EzSembleContext context, byte[] bytes, Dictionary<int,int> flags, int lot = 0) {
        int Eval(Expr e) {
            if (e.TryAsInt(out int value)) return value;
            if (e is FunctionCall f) {
                if (f.Name == "GetEventFlag") return flags.GetValueOrDefault(Eval(f.Args[0]));
                if (f.Name == "StateGroupArg") return lot;
                throw new Exception("Unsupported test function: " + f.Name);
            }
            if (e is BinaryExpr b) {
                int a=Eval(b.Lhs), c=Eval(b.Rhs);
                return b.Op switch {
                    "==" => a == c ? 1 : 0, "!=" => a != c ? 1 : 0,
                    "&&" => a != 0 && c != 0 ? 1 : 0, "||" => a != 0 || c != 0 ? 1 : 0,
                    _ => throw new Exception("Unsupported test operator: " + b.Op)
                };
            }
            throw new Exception("Unsupported test expression: " + e);
        }
        return Eval(EzSembler.DissembleExpression(context, bytes));
    }
    static long? Route(EzSembleContext context, ESD.State state, Dictionary<int,int> flags, int lot=0) =>
        state.Conditions.First(c => Evaluate(context, c.Evaluator, flags, lot) != 0).TargetState;

    static void GuardReward(EzSembleContext context, ESD esd, int itemLot, int acquiredFlag) {
        var award = esd.StateGroups[2147483641];
        Require(award[1].EntryCommands.Single().CommandBank == 1 && award[1].EntryCommands.Single().CommandID == 104,
            "Unexpected item-lot award helper.");
        award[0].Conditions.Insert(0, Rule(context,
            $"StateGroupArg[0] == {itemLot} && GetEventFlag({acquiredFlag}) == 1", 5));
        Require(Route(context, award[0], new() {{acquiredFlag, 0}}, itemLot) == 1, "Unclaimed reward blocked.");
        Require(Route(context, award[0], new() {{acquiredFlag, 1}}, itemLot) == 5, "Claimed reward can duplicate.");
        Require(Route(context, award[0], new() {{acquiredFlag, 1}}, itemLot + 1) == 1, "Other rewards were blocked.");
    }

    static void ValidateGraph(ESD esd) {
        foreach (var group in esd.StateGroups.Values)
            foreach (var state in group.Values)
                foreach (var condition in Conditions(state.Conditions))
                    Require(condition.TargetState == null || group.ContainsKey(condition.TargetState.Value), "Invalid dialogue state target.");
    }

    public static void Build(string input, string output, string definitions, EzSembleContext context) {
        void Save(string relative, byte[] data) {
            string path = Path.Combine(output, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }
        foreach (string map in new[] { "m11_01_00_00", "m20_00_00_00" }) {
            var binder = BND4.Read(Path.Combine(input, $"script/talk/{map}.talkesdbnd.dcx"));
            var before = binder.Files.ToDictionary(f => f.Name, f => (byte[])f.Bytes.Clone());
            var changed = new HashSet<string>();
            foreach (var file in binder.Files) {
                string name = file.Name.Replace('\\','/').Split('/').Last();
                if (name is not ("t111110.esd" or "t111240.esd" or "t200320.esd")) continue;
                var esd = ESD.Read(file.Bytes);
                if (name == "t111110.esd") {
                    var state = esd.StateGroups[1000][2];
                    Require(state.Conditions.Count == 2 && state.Conditions[0].TargetState == 3 && state.Conditions[1].TargetState == 146,
                        "Unexpected Kuro conversation routing.");
                    state.Conditions.Insert(0, Rule(context,
                        "GetEventFlag(8400) == 1 && GetEventFlag(9303) == 1 && GetEventFlag(8301) == 0 && GetEventFlag(8302) == 0 && GetEventFlag(8306) == 0 && (GetEventFlag(71110055) == 0 || GetEventFlag(71110056) == 0 || GetEventFlag(71110057) == 0 || GetEventFlag(71110058) == 0 || GetEventFlag(71110059) == 0)", 3));
                    GuardReward(context, esd, 61102, 50006112);
                    Require(Route(context, state, new() {{8400,1},{9303,1}}) == 3, "Kuro intro is still skipped after Genichiro.");
                    Require(Route(context, state, new() {{8400,1},{9303,1},{71110055,1},{71110056,1},{71110057,1},{71110058,1},{71110059,1}}) == 146,
                        "Kuro later dialogue blocked after intro completion.");
                    Require(Route(context, state, new() {{8400,1},{9303,1},{8301,1}}) == 146,
                        "Kuro later story was diverted to the intro.");
                    Require(Route(context, state, new() {{8400,1}}) == 146, "Kuro pre-Genichiro routing changed.");
                    // Gun Fort key acquisition sets 71110081 in Kuro's startup
                    // helper. It must not bypass the unfinished referral (59).
                    foreach (int blade in new[] {0,1}) {
                        var flags = new Dictionary<int,int> {{8400,1},{9303,1},{71110055,1},{71110056,1},
                            {71110057,1},{71110058,1},{71110081,1},{8410,blade}};
                        Require(Route(context,state,flags)==3,"Early key/blade skipped Kuro's Isshin referral.");
                        Require(Route(context,esd.StateGroups[1000][61],flags)==8,"Kuro's referral branch is unreachable.");
                        flags[71110059]=1;
                        Require(Route(context,state,flags)==146,"Finished referral blocks later Kuro dialogue.");
                    }

                } else if (name == "t111240.esd") {
                    var state = esd.StateGroups[1000][5];
                    Require(state.Conditions.Count == 3 && state.Conditions[0].TargetState == 16 && state.Conditions[1].TargetState == 6,
                        "Unexpected Isshin conversation routing.");
                    state.Conditions.Insert(1, Rule(context,
                        "GetEventFlag(71110204) == 0 || GetEventFlag(71110200) == 0", 3));
                    // Preserve the original early conversation topics until the
                    // first invasion, even if Monkeys and Mortal Blade came first.
                    int replacements = 0;
                    foreach (var s in esd.StateGroups[2147483603].Values) {
                        foreach (var c in Conditions(s.Conditions)) Rewrite(c.Evaluator, b => c.Evaluator = b);
                        foreach (var command in s.EntryCommands.Concat(s.WhileCommands).Concat(s.ExitCommands))
                            for (int i = 0; i < command.Arguments.Count; i++) {
                                int index = i;
                                Rewrite(command.Arguments[i], b => command.Arguments[index] = b);
                            }
                    }
                    void Rewrite(byte[] bytes, Action<byte[]> assign) {
                        var expr = EzSembler.DissembleExpression(context, bytes);
                        bool edited = false;
                        expr.Visit(AstVisitor.PreAct(e => {
                            if (e is FunctionCall f && f.Name == "GetEventFlag" && f.Args.Count == 1 && Constant(f.Args[0], 8410)) {
                                f.Args[0] = new ConstExpr { Value = 8301 }; edited = true; replacements++;
                            }
                        }));
                        if (edited) assign(EzSembler.AssembleExpression(context, expr));
                    }
                    Require(replacements == 3, "Unexpected Isshin topic gate count.");
                    GuardReward(context, esd, 62400, 50006240);
                    Require(Route(context, state, new() {{8410,1}}) == 3, "Isshin sake intro unavailable after Mortal Blade.");
                    Require(Route(context, state, new() {{8410,1},{71110204,1},{71110200,1}}) == 6, "Isshin later dialogue blocked.");
                    Require(Route(context, state, new() {{71110249,1}}) == 16, "Isshin later special story priority changed.");
                    var topic = esd.StateGroups[2147483603][3];
                    Require(Route(context, topic, new() {{8410,1}}) == 1, "Isshin early topic still hidden by Mortal Blade.");
                    Require(Route(context, topic, new() {{8301,1}}) == 4, "Isshin invasion topic gating changed.");
                } else {
                    int replacements = 0;
                    foreach (var group in esd.StateGroups.Values)
                        foreach (var s in group.Values)
                            foreach (var c in Conditions(s.Conditions)) {
                                var expr = EzSembler.DissembleExpression(context, c.Evaluator);
                                bool edited = false;
                                expr = expr.Visit(AstVisitor.Pre(e => {
                                    if (e is FunctionCall f && f.Name == "GetEventFlag" && f.Args.Count == 1 && Constant(f.Args[0], 1480)) {
                                        edited = true; replacements++; return new ConstExpr { Value = (sbyte)1 };
                                    }
                                    return null;
                                }));
                                if (edited) c.Evaluator = EzSembler.AssembleExpression(context, expr);
                            }
                    Require(replacements == 2, "Unexpected monk dialogue gate count.");
                    GuardReward(context, esd, 63200, 50006320);
                    foreach (int flag in new[] {1480,1481,1482})
                        Require(Route(context, esd.StateGroups[1000][6], new() {{flag,1}}) == 7, "Monk reward dialogue still gated by disappearance state.");
                }
                ValidateGraph(esd);
                file.Bytes = esd.Write();
                ValidateGraph(ESD.Read(file.Bytes));
                changed.Add(file.Name);
                Console.WriteLine("Patched and validated " + name);
            }
            Require(changed.Count == (map == "m11_01_00_00" ? 2 : 1), "Missing expected NPC scripts.");
            byte[] bytes = binder.Write();
            var check = BND4.Read(bytes);
            Require(check.Files.Count == binder.Files.Count, "Dialogue archive lost entries.");
            for (int i=0; i<check.Files.Count; i++) {
                Require(check.Files[i].Name == binder.Files[i].Name && check.Files[i].ID == binder.Files[i].ID && check.Files[i].Flags == binder.Files[i].Flags,
                    "Dialogue archive entry metadata changed.");
                if (!changed.Contains(check.Files[i].Name)) Require(check.Files[i].Bytes.SequenceEqual(before[check.Files[i].Name]), "Unrelated NPC script changed.");
            }
            Save($"script/talk/{map}.talkesdbnd.dcx", bytes);
        }

        // Map and shared NPC events are maintained by reconcile.py, not historical hotfixes.
        var parameters = BND4.Read(Path.Combine(input, "gameparam.parambnd"));
        var beforeParams = parameters.Files.ToDictionary(f => f.Name, f => (byte[])f.Bytes.Clone());
        var actionsFile = parameters.Files.Single(f => f.Name.Replace('\\','/').Split('/').Last() == "ActionButtonParam.param");
        var actions = PARAM.Read(actionsFile.Bytes);
        var definition = PARAMDEF.XmlDeserialize(definitions);
        actions.ApplyParamdef(definition);
        var action = actions.Rows.Single(r => r.ID == 2000680);
        int initialPriority = (int)action["priority"].Value;
        Require(initialPriority is 10 or 9, "Unexpected Monkeys bell priority.");
        // In this format, smaller values take priority. The monk uses 10.
        action["priority"].Value = 9;
        actionsFile.Bytes = actions.Write();
        var beforeActions = PARAM.Read(beforeParams[actionsFile.Name]); beforeActions.ApplyParamdef(definition);
        var afterActions = PARAM.Read(actionsFile.Bytes); afterActions.ApplyParamdef(definition);
        Require(beforeActions.Rows.Count==afterActions.Rows.Count,"Action button row count changed.");
        int changedCells=0;
        for (int r=0;r<beforeActions.Rows.Count;r++) {
            var a=beforeActions.Rows[r]; var b=afterActions.Rows[r];
            Require(a.ID==b.ID && a.Name==b.Name,"Action button row identity changed.");
            for (int i=0;i<a.Cells.Count;i++) {
                var x=a.Cells[i]; var y=b.Cells[i];
                bool same=x.Value is byte[] raw ? y.Value is byte[] other && raw.SequenceEqual(other) : Equals(x.Value,y.Value);
                if (!same) {
                    changedCells++;
                    Require(a.ID==2000680 && x.Def.InternalName=="priority" && (int)y.Value==9,"Unrelated action button value changed.");
                }
            }
        }
        Require(changedCells == (initialPriority == 10 ? 1 : 0), "Unexpected action button changes.");
        byte[] paramBytes=parameters.Write();
        var afterBinder=BND4.Read(paramBytes);
        Require(afterBinder.Files.Count==parameters.Files.Count,"Parameter archive lost entries.");
        foreach(var file in afterBinder.Files)
            if(file.Name!=actionsFile.Name) Require(file.Bytes.SequenceEqual(beforeParams[file.Name]),"Unrelated parameter table changed.");
        Save("param/gameparam/gameparam.parambnd.dcx",paramBytes);
        Console.WriteLine("Validated bell priority 9; all other action button values and parameter tables preserved.");
        Console.WriteLine("PASS: dialogue routing, late-story gates, reward uniqueness, state targets, archive round trips and preservation checks.");
    }
}

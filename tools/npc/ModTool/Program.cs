using SoulsFormats;
using ESDLang.Doc;
using ESDLang.EzSemble;
using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
string dependencies = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.deps"));
if (args.Length != 3 || args[0] is not ("build" or "compare" or "verify-parameters"))
    throw new ArgumentException("Usage: ModTool build RAW_INPUT NEW_OUTPUT | compare REBUILT_RAW CANONICAL_RAW | verify-parameters OLD_RAW_BND NEW_RAW_BND");
string input = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);
if (args[0] == "verify-parameters") {
    DialoguePatches.VerifyParameters(input, output, Path.Combine(dependencies, "ActionButtonParam.xml"));
    return;
}
if (args[0] == "compare") {
    foreach (var relative in new[] { "script/talk/m11_01_00_00.talkesdbnd.dcx", "script/talk/m20_00_00_00.talkesdbnd.dcx", "param/gameparam/gameparam.parambnd.dcx" })
        DialoguePatches.CompareArchives(Path.Combine(input, relative), Path.Combine(output, relative));
    Console.WriteLine("PASS: rebuilt dialogue scripts and parameters match canonical archive contents.");
    return;
}
if (Directory.Exists(output) || File.Exists(output))
    throw new ArgumentException("Output must not already exist.");
var context = new EzSembleContext {
    Doc = ESDDocumentation.DeserializeFromFile(Path.Combine(dependencies, "esdlang/dist/ESDScriptingDocumentation_Talk.json"),
        new ESDDocumentation.DocOptions { Game = "sdt" })
};
DialoguePatches.Build(input, output, Path.Combine(dependencies, "ActionButtonParam.xml"), context);

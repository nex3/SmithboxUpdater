using SoulsFormats;
using System.IO;
using System.Text.Json;
using Andre.IO.VFS;

var gamePath = "C:\\Users\\Natalie\\SteamLibrary\\steamapps\\common\\ELDEN RING NIGHTREIGN\\Game\\";
var smithboxAssetPath = "D:\\Natalie\\Code\\Smithbox\\src\\Smithbox.Data\\Assets";

var paramsWeCareAbout = new HashSet<string>([
    "NpcParam", "ItemLotParam_enemy", "ItemTableParam", "EquipParamAccessory",
    "EquipParamCustomWeapon", "EquipParamAntique", "EquipParamProtector",
    "EquipParamWeapon", "EquipParamGoods", "SwordArtsTableParam", "AttachEffectTableParam",
    "MagicTableParam", "AttachEffectParam", "Magic"
]);

Console.WriteLine("Loading params...");

var paramdefs = new Dictionary<string, PARAMDEF>();
foreach (var file in Directory.GetFiles(
    Path.Join(smithboxAssetPath, $"PARAM\\NR\\Defs"),
    "*.xml"
))
{
    var paramdef = PARAMDEF.XmlDeserialize(file)!;
    paramdefs[paramdef.ParamType] = paramdef;
}

var bnd = SFUtil.DecryptNightreignRegulation($"{gamePath}\\regulation.bin");
var paramDict = new Dictionary<string, PARAM>();
foreach (var file in bnd.Files)
{
    if (!file.Name.ToUpper().EndsWith(".PARAM")) continue;

    var paramName = Path.GetFileNameWithoutExtension(file.Name);
    if (!paramsWeCareAbout.Contains(paramName)) continue;

    var param = PARAM.ReadIgnoreCompression(file.Bytes);

    param.ApplyParamdef(paramdefs[param.ParamType]);
    paramDict[paramName] = param;
}

var communityRowNamesPath = Path.Join(smithboxAssetPath, $"PARAM\\NR\\Community Row Names.json");
var communityRowNames =
    JsonSerializer.Deserialize<RowNameStore>(File.ReadAllText(communityRowNamesPath))!;

var paramNames = new Dictionary<string, ILookup<int, RowNameEntry>>();
foreach (var param in communityRowNames.Params)
{
    var name = param.Name;
    if (!paramsWeCareAbout.Contains(name)) continue;
    paramNames[name] = param.Entries.ToLookup(e => e.ID, e => e);
}

Console.WriteLine("Params loaded!");

Directory.SetCurrentDirectory(smithboxAssetPath + "\\..");

var vfs = ArchiveBinderVirtualFileSystem.FromGameFolder(gamePath, Andre.Core.Game.NR);
var containerBytes = vfs.ReadFileOrThrow("/msg/engus/item.msgbnd.dcx");
var fmgsByName = new Dictionary<string, FMG>();
using (var binder = BND4.Read(containerBytes))
{
    foreach (var file in binder.Files)
    {
        if (file.Name.Contains(".fmg"))
        {
            var fmgName = Path.GetFileName(file.Name.Replace(".fmg", ""));
            var fmg = FMG.Read(file.Bytes);
            fmgsByName[fmgName] = fmg;
        }
    }
}

Console.WriteLine("FMG loaded!");

void deannotateSingleValueTables(string name)
{
    var names = paramNames[name];
    foreach (var pair in names)
    {
        if (pair.Count() != 1) continue;
        var row = pair.First();
        row.Name = row.Name.Replace("<Table> ", "");
    }
}

foreach (var name in paramsWeCareAbout)
{
    if (name.Contains("Table"))
    {
        deannotateSingleValueTables(name);
    }
}

var options = new JsonSerializerOptions();
options.NewLine = "\n";
options.WriteIndented = true;
options.IndentSize = 2;
File.WriteAllText(
    communityRowNamesPath,
    JsonSerializer.Serialize(communityRowNames, options)
);

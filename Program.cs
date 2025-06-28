using SoulsFormats;
using System.IO;
using System.Text.Json;
using Andre.IO.VFS;
using System.Text.RegularExpressions;
using System.Diagnostics;

var gamePath = "C:\\Users\\Natalie\\SteamLibrary\\steamapps\\common\\ELDEN RING NIGHTREIGN\\Game\\";
var smithboxAssetPath = "D:\\Natalie\\Code\\Smithbox\\src\\Smithbox.Data\\Assets";

var paramsWeCareAbout = new HashSet<string>([
    "NpcParam", "ItemLotParam_enemy", "ItemTableParam", "EquipParamAccessory",
    "EquipParamCustomWeapon", "EquipParamAntique", "EquipParamProtector",
    "EquipParamWeapon", "EquipParamGoods", "SwordArtsTableParam", "AttachEffectTableParam",
    "MagicTableParam", "AttachEffectParam", "Magic", "SwordArtsParam"
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
var paramNamesByIndex = new Dictionary<string, Dictionary<int, RowNameEntry>>();
foreach (var param in communityRowNames.Params)
{
    var name = param.Name;
    if (!paramsWeCareAbout.Contains(name)) continue;
    paramNames[name] = param.Entries.ToLookup(e => e.ID, e => e);
    paramNamesByIndex[name] = param.Entries
        .Select((value, i) => new { value, i })
        .ToDictionary(pair => pair.i, pair => pair.value);
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

string? getTableName(string name)
{
    return new Regex("^(<[^>]+>)( |$)").Match(name).Groups[1]?.Value;
}

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

string? getReferentName(int id, int category)
{
    if (id == 0 || category == 0) return null;
    var referent = paramNames[category switch
    {
        1 => "EquipParamGoods",
        2 => "EquipParamWeapon",
        3 => "EquipParamProtector",
        4 => "EquipParamAccessory",
        5 => "EquipParamAntique",
        6 => "EquipParamCustomWeapon",
        7 => "ItemTableParam",
        _ => throw new NotImplementedException()
    }];
    var entries = referent[id];
    var first = entries.FirstOrDefault()?.Name;
    if (first == null) return null;
    if (entries.Count() == 1) return category == 6 ? $"Custom {first}" : first;
    if (first.StartsWith('<')) return getTableName(first)![1..^1];
    return "<Table>";
}

IEnumerable<(RowNameEntry, PARAM.Row)> alignRows(string name)
{
    var paramNames = paramNamesByIndex[name];
    var paramJson = communityRowNames.Params.Find(param => param.Name == name)!;
    foreach (var (row, i) in paramDict[name].Rows.Select((row, i) => (row, i)))
    {
        if (i >= paramNames.Count)
        {
            var newRow = new RowNameEntry()
            {
                Name = "",
                ID = row.ID,
                Index = i,
            };
            paramNames[i] = newRow;
            paramJson.Entries.Add(newRow);
        }

        var entry = paramNames[i];
        while (row.ID > entry.ID)
        {
            paramJson.Entries.RemoveAt(i);
            for (var j = i; j < paramJson.Entries.Count; j++)
            {
                paramJson.Entries[j].Index = j;
                paramNames[j] = paramNames[j + 1];
            }
            paramNames.Remove(paramJson.Entries.Count);
            entry = paramNames[i];
        }

        if (row.ID < entry.ID)
        {
            // Not efficient but it'll only happen once per each batch of new IDs
            entry = new RowNameEntry()
            {
                Name = "",
                ID = row.ID,
                Index = i,
            };
            paramJson.Entries.Insert(i, entry);
            paramNames[i] = entry;
            for (var j = i + 1; j < paramJson.Entries.Count; j++)
            {
                paramJson.Entries[j].Index = j;
                paramNames[j] = paramJson.Entries[j];
            }
        }

        yield return (entry, row);
    }
}

foreach (var (entry, row) in alignRows("ItemTableParam"))
{
    var prefix = getTableName(entry.Name) ?? "";
    var referent = getReferentName((int)row["itemId"].Value, (int)row["itemCategory"].Value);
    if (referent == null) continue;
    entry.Name = $"{prefix} {referent}".Trim();
}

foreach (var (entry, row) in alignRows("AttachEffectTableParam"))
{
    var prefix = getTableName(entry.Name) ?? "";
    var referent = paramNames["AttachEffectParam"][(int)row["attachEffectId"].Value].FirstOrDefault();
    if (referent == null) continue;
    var name = referent.Name.Replace("Relic: ", "").Replace("Weapon Effect: ", "");
    entry.Name = $"{prefix} {name}".Trim();
}

foreach (var (entry, row) in alignRows("MagicTableParam"))
{
    var prefix = getTableName(entry.Name) ?? "";
    var referent = paramNames["Magic"][(int)row["magicId"].Value].FirstOrDefault();
    if (referent == null) continue;
    entry.Name = $"{prefix} {referent.Name}".Trim();
}

foreach (var (entry, row) in alignRows("SwordArtsTableParam"))
{
    var prefix = getTableName(entry.Name) ?? "";
    var referent = paramNames["SwordArtsParam"][(int)row["swordArtsId"].Value].FirstOrDefault();
    if (referent == null) continue;
    var name = referent.Name;
    entry.Name = $"{prefix} {name}".Trim();
}

foreach (var name in paramsWeCareAbout)
{
    if (!name.Contains("Table")) continue;

    foreach (var (entry, row) in alignRows(name))
    {
        var weight = row["chanceWeight"].Value;
        if (name switch { "ItemTableParam" => (ushort)weight, _ => (int)weight } != 0) continue;
        var prefix = new Regex("^(<[^>]+>)( |$)").Match(entry.Name).Groups[1]?.Value ?? "";
        entry.Name = $"{prefix} (0% weight)".Trim();
    }
}

foreach (var param in paramsWeCareAbout)
{
    alignRows(param).ToList();
}

var options = new JsonSerializerOptions();
options.NewLine = "\n";
options.WriteIndented = true;
options.IndentSize = 2;
File.WriteAllText(
    communityRowNamesPath,
    JsonSerializer.Serialize(communityRowNames, options)
);

using SoulsFormats;
using System.IO;
using System.Text.Json;
using Andre.IO.VFS;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Collections.Generic;
using DotNext.Collections.Generic;

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
    if (category == 7 && first.StartsWith('<')) return getTableName(first)![1..^1];
    if (entries.Count() == 1)
    {
        if (category != 6) return first;
        if (first.StartsWith('[')) return first;
        return $"[Custom] {first}";
    }
    return "<Table>";
}

IEnumerable<(RowNameEntry, PARAM.Row)> alignRows(string name)
{
    var paramJson = communityRowNames.Params.Find(param => param.Name == name);
    if (paramJson == null)
    {
        paramJson = new() { Name = name, Entries = [] };
        communityRowNames.Params.Add(paramJson);
    }

    var rows = new Queue<PARAM.Row>(paramDict[name].Rows);
    var entries = new Queue<RowNameEntry>(paramJson.Entries);
    paramJson.Entries = [];

    while (rows.TryDequeue(out var row))
    {
        if (entries.TryPeek(out var entry) && row.ID >= entry.ID)
        {
            entries.Dequeue();
            if (row.ID == entry.ID)
            {
                entry.Index = paramJson.Entries.Count;
                paramJson.Entries.Add(entry);
                yield return (entry, row);
            }
        }
        else
        {
            var newEntry = new RowNameEntry()
            {
                Name = "",
                ID = row.ID,
                Index = paramJson.Entries.Count
            };
            paramJson.Entries.Add(newEntry);
            yield return (newEntry, row);
            continue;
        }
    }
}

Dictionary<string, double> dropProbabilities(int itemTableId)
{
    var names = paramNames["ItemTableParam"];
    var weights = paramDict["ItemTableParam"].Rows
        .Where(row => row.ID == itemTableId && (ushort)row["chanceWeight"].Value > 0)
        .Zip(names[itemTableId].Select(entry =>
            new Regex("^(<[^>]+>)? ?(Custom)? ").Replace(entry.Name, "")
        ))
        .SelectMany(pair =>
        {
            var (row, name) = pair;
            var weight = (double)(ushort)row["chanceWeight"].Value;
            return (int)row["itemCategory"].Value == 7
                ? dropProbabilities((int)row["itemId"].Value)
                    .Select(pair => (pair.Key, pair.Value * weight))
                : [(name, weight)];
        });

    Dictionary<string, double> probabilities = [];
    var totalWeight = weights.Sum(pair => pair.Item2);
    foreach (var (name, weight) in weights)
    {
        probabilities.TryAdd(name, 0);
        probabilities[name] += weight / totalWeight;
    }
    return probabilities;
}

var tablesThatContainCustomWeapons = paramDict["ItemTableParam"].Rows
    .Where(row => (int)row["itemCategory"].Value == 6)
    .ToLookup(row => (int)row["itemId"].Value);

string getWeaponType(int customWeaponId, List<string>? allowedTypes = null)
{
    var tables = tablesThatContainCustomWeapons[customWeaponId]
        .Select(row =>
        {
            var tableName = getTableName(paramNames["ItemTableParam"][row.ID].First().Name)!;
            if (tableName.EndsWith(" Madness>")) return "Madness";
            var match = new Regex("^<(Rare|Uncommon|Common) ([^ ]+) [^ ].*>$")
                .Match(tableName);
            return match.Success ? match.Groups[2].Value : null;
        })
        .Where(type => type != null)
        .ToHashSet();

    List<string> typeOrder = allowedTypes ?? ["Sleep", "Rot", "Frost", "Poison", "Blood", "Madness", "Occult", "Holy", "Lightning", "Fire", "Magic"];
    foreach (var potentialType in typeOrder)
    {
        if (tables.Contains(potentialType)) return potentialType;
    }
    return "Untyped";
}

Dictionary<string, double> typeProbabilities(int itemTableId, List<string>? allowedTypes = null, int? separateType = null)
{
    Dictionary<string, double> probabilities = [];
    foreach (var (nameWithPrefix, prob) in dropProbabilities(itemTableId))
    {
        var match = new Regex("^\\[([^\\]]+)\\] (.*)").Match(nameWithPrefix);
        var type = match.Groups[1]!.Value;
        var name = match.Groups[2]!.Value;
        var row = alignRows("EquipParamCustomWeapon")
            .First(pair => pair.Item1.Name == name && pair.Item2.ID >= 1000000)
            .Item2;

        if (
            separateType != null &&
            (ushort)(
                paramDict["EquipParamWeapon"]
                    [(int)row["targetWeaponId"].Value]
                    ["wepType"]
                    .Value
            ) == separateType
        )
        {
            type = "Weapon Type";
        }
        else if (
            type == "Custom" ||
            (
                type != "Untyped" &&
                !(allowedTypes?.Contains(type) ?? true)
            )
        )
        {
            type = getWeaponType(row.ID, allowedTypes);
        }
        probabilities.TryAdd(type, 0);
        probabilities[type] += prob;
    }
    return probabilities;
}

Dictionary<string, double> typeCategoryProbabilities(int itemTableId)
{
    var probs = typeProbabilities(7120052);
    var processed = new Dictionary<string, double>();
    processed["Untyped"] = probs["Untyped"];
    processed["Elemental"] = probs["Magic"] + probs["Fire"] + probs["Lightning"] + probs["Holy"];
    processed["Status"] = probs["Frost"] + probs["Poison"] + probs["Blood"] + probs["Sleep"] + probs["Occult"];
    if (probs.ContainsKey("Madness")) processed["Status"] += probs["Madness"];
    if (probs.ContainsKey("Rot")) processed["Status"] += probs["Rot"];
    return processed;
}

void weakAffinityProbabilities()
{
    var probsUncFireClass = typeProbabilities(7900500, ["Fire"]);
    var probsUncHolyClass = typeProbabilities(7900700, ["Holy"]);

    var probsUncFire = typeProbabilities(7140552, ["Fire"]);
    var probsUncHoly = typeProbabilities(7140752, ["Holy"]);

    var probsRareFire = typeProbabilities(7120553, ["Fire"]);
    var probsRareHoly = typeProbabilities(7130753, ["Holy"]);

    var untypedUncClass = (probsUncHolyClass["Untyped"] + probsUncFireClass["Untyped"]) / 2;
    var typedUncClass = 1 - untypedUncClass;
    var untypedUnc = (probsUncHoly["Untyped"] + probsUncFire["Untyped"]) / 2;
    var typedUnc = 1 - untypedUnc;
    var untypedRare = (probsRareHoly["Untyped"] + probsRareFire["Untyped"]) / 2;
    var typedRare = 1 - untypedRare;
    Debugger.Break();

}

void strongAffinityProbabilities()
{
    var probsUncFireClass = typeProbabilities(7900501, ["Fire"]);
    var probsUncMagicClass = typeProbabilities(7900801, ["Magic"], separateType: 57);
    var probsUncHolyClass = typeProbabilities(7900701, ["Holy"]);
    var probsUncLightningClass = typeProbabilities(7900601, ["Lightning"]);

    var probsUncFire = typeProbabilities(7120552, ["Fire"]);
    var probsUncMagic = typeProbabilities(7130852, ["Magic"], separateType: 57);
    var probsUncHoly = typeProbabilities(7130752, ["Holy"]);
    var probsUncLightning = typeProbabilities(7130652, ["Lightning"]);

    var probsRareFire = typeProbabilities(7120553, ["Fire"]);
    var probsRareMagic = typeProbabilities(7130853, ["Magic"], separateType: 57);
    var probsRareHoly = typeProbabilities(7130753, ["Holy"]);
    var probsRareLightning = typeProbabilities(7130653, ["Lightning"]);

    var untypedUncElementalClass = (probsUncHolyClass["Untyped"] + probsUncFireClass["Untyped"] + probsUncLightningClass["Untyped"]) / 3;
    var typedUncElementalClass = 1 - untypedUncElementalClass;
    var untypedUncElemental = (probsUncHoly["Untyped"] + probsUncFire["Untyped"] + probsUncLightning["Untyped"]) / 3;
    var typedUncElemental = 1 - untypedUncElemental;
    var untypedRareElemental = (probsRareHoly["Untyped"] + probsRareFire["Untyped"] + probsRareLightning["Untyped"]) / 3;
    var typedRareElemental = 1 - untypedRareElemental;

    var probsUncPoisonClass = typeProbabilities(7901001, ["Poison"]);
    var probsUncBloodClass = typeProbabilities(7901101, ["Blood"]);
    var probsUncSleepClass = typeProbabilities(7901401, ["Sleep"]);
    var probsUncFrostClass = typeProbabilities(7900901, ["Frost"]);

    var probsUncPoison = typeProbabilities(7131052, ["Poison"]);
    var probsUncBlood = typeProbabilities(7131152, ["Blood"]);
    var probsUncSleep = typeProbabilities(7131453, ["Sleep"]);
    var probsUncFrost = typeProbabilities(7130952, ["Frost"]);

    var probsRarePoison = typeProbabilities(7131053, ["Poison"]);
    var probsRareBlood = typeProbabilities(7131153, ["Blood"]);
    var probsRareSleep = typeProbabilities(7131453, ["Sleep"]);
    var probsRareFrost = typeProbabilities(7130953, ["Frost"]);

    var untypedUncStatusClass = (probsUncPoisonClass["Untyped"] + probsUncBloodClass["Untyped"] + probsUncSleepClass["Untyped"] + probsUncFrostClass["Untyped"]) / 4;
    var typedUncStatusClass = 1 - untypedUncStatusClass;
    var untypedUncStatus = (probsUncPoison["Untyped"] + probsUncBlood["Untyped"] + probsUncSleep["Untyped"] + probsUncFrost["Untyped"]) / 4;
    var typedUncStatus = 1 - untypedUncStatus;
    var untypedRareStatus = (probsRarePoison["Untyped"] + probsRareBlood["Untyped"] + probsRareSleep["Untyped"] + probsRareFrost["Untyped"]) / 4;
    var typedRareStatus = 1 - untypedRareStatus;

    var untypedUncClass = (probsUncHolyClass["Untyped"] + probsUncFireClass["Untyped"] + probsUncLightningClass["Untyped"] + probsUncPoisonClass["Untyped"] + probsUncBloodClass["Untyped"] + probsUncSleepClass["Untyped"] + probsUncFrostClass["Untyped"]) / 7;
    var typedUncClass = 1 - untypedUncClass;
    var untypedUnc = (probsUncHoly["Untyped"] + probsUncFire["Untyped"] + probsUncLightning["Untyped"] + probsUncPoison["Untyped"] + probsUncBlood["Untyped"] + probsUncSleep["Untyped"] + probsUncFrost["Untyped"]) / 7;
    var typedUnc = 1 - untypedUnc;
    var untypedRare = (probsRarePoison["Untyped"] + probsRareBlood["Untyped"] + probsRareSleep["Untyped"] + probsRareFrost["Untyped"] + probsRareHoly["Untyped"] + probsRareFire["Untyped"] + probsRareLightning["Untyped"]) / 7;
    var typedRare = 1 - untypedRare;
    Debugger.Break();

}

string weaponTypeForWeapon(PARAM.Row row)
{
    if (row.ID >= 40000000 && row.ID < 41000000) return "Light Bow";
    return (ushort)row["wepType"].Value switch
    {
        1 => "Dagger",
        3 => "Straight Sword",
        5 => "Greatsword",
        7 => "Colossal Sword",
        9 => "Curved Sword",
        11 => "Curved Greatsword",
        13 => "Katana",
        14 => "Twinblade",
        15 => "Thrusting Sword",
        16 => "Heavy Thrusting Sword",
        17 => "Axe",
        19 => "Greataxe",
        21 => "Hammer",
        23 => "Great Hammer",
        24 => "Flail",
        25 => "Spear",
        28 => "Heavy Spear",
        29 => "Halberd",
        31 => "Scythe",
        35 => "Fist",
        37 => "Claw",
        39 => "Whip",
        41 => "Colossal Weapon",
        50 => "Light Bow",
        51 => "Bow",
        53 => "Greatbow",
        55 => "Crossbow",
        56 => "Ballista",
        57 => "Staff",
        61 => "Seal",
        65 => "Small Shield",
        67 => "Medium Shield",
        69 => "Greatshield",
        81 => "Arrow",
        83 => "Greatarrow",
        85 => "Bolt",
        86 => "Ballista Bolt",
        87 => "Torch",
        88 => "Hand-to-Hand",
        89 => "Perfume Bottle",
        90 => "Thrusting Shield",
        91 => "Throwing Blade",
        92 => "Reverse-hand Blade",
        93 => "Light Greatsword",
        94 => "Great Katana",
        95 => "Beast Claw",
        _ => throw new NotImplementedException()
    };
}

string? weaponTypeForCustomWeapon(PARAM.Row row)
{
    var weapon = paramDict["EquipParamWeapon"][(int)row["targetWeaponId"].Value];
    if (weapon == null) return null;
    return weaponTypeForWeapon(weapon);
}

string? weaponTypeForItemTable(PARAM.Row row)
{
    if ((int)row["itemCategory"].Value != 6)
    {
        throw new Exception("Can't find a weapon type for a non-weapon");
    }

    return weaponTypeForCustomWeapon(
        paramDict["EquipParamCustomWeapon"][(int)row["itemId"].Value]!
    );
}

var customWeaponsByArtsId =
    paramDict["EquipParamCustomWeapon"].Rows.ToLookup(row => (int)row["swordArtsTableId"].Value);

string weaponTypeForArtsTable(PARAM.Row row)
{
    // This group includes Wylder's Greatsword but that's and edge case
    if (row.ID == 20000000) return "Short Sword";

    var customWeaponTypes = customWeaponsByArtsId[row.ID]
        .Select(weaponTypeForCustomWeapon)
        .Where(row => row != null)
        .ToHashSet();
    if (customWeaponTypes.Count == 1) return customWeaponTypes.First();
    throw new Exception($"Multiple weapon types for arts row {row.ID}");
}

string? enumToElement(int number)
{
    return number switch
    {
        1 => "Elemental",
        5 => "Fire",
        6 => "Lightning",
        7 => "Holy",
        8 => "Magic",
        9 => "Frost",
        10 => "Poison",
        11 => "Blood",
        12 => "Occult",
        13 => "Rot",
        14 => "Sleep",
        15 => "Madness",
        _ => null
    };
}

string? bigEnumToElement(int number)
{
    return number switch
    {
        5 => "Untyped",
        10 => "Elemental",
        50 => "Fire",
        60 => "Lightning",
        70 => "Holy",
        80 => "Magic",
        90 => "Frost",
        100 => "Poison",
        110 => "Blood",
        120 => "Occult",
        130 => "Rot",
        140 => "Sleep",
        150 => "Madness",
        _ => null
    };
}

string? enumToWeaponAffinity(int number)
{
    return number switch
    {
        50 => "Untyped",
        60 => "Rot",
        70 => "Sleep",
        100 => "Elemental",
        500 => "Fire",
        600 => "Lightning",
        700 => "Holy",
        800 => "Magic",
        900 => "Frost",
        1000 => "Poison",
        1100 => "Blood",
        1200 => "Occult",
        _ => null
    };
}

string? enumToRarity(int number)
{
    return number switch
    {
        1 => "Common",
        2 => "Uncommon",
        3 => "Rare",
        4 => "Legendary",
        8 => "Cursed",
        11 => "Common-",
        12 => "Common~",
        13 => "Common+",
        24 => "Uncommon-",
        25 => "Uncommon~",
        26 => "Uncommon+",
        _ => null
    };
}

(string?, string?) enumToRarityAndElement(int number)
{
    return (
        number >= 5000
            ? "Cursed"
            : enumToRarity(number % 50),
        number >= 5000
            ? bigEnumToElement((number - 5000) / 50 * 5)
            : bigEnumToElement(number / 50 * 5)
    );
}

foreach (var (entry, row) in alignRows("ItemTableParam"))
{
    var prefix = getTableName(entry.Name) ?? "";
    var referent = getReferentName((int)row["itemId"].Value, (int)row["itemCategory"].Value);
    if (referent == null) continue;

    if (prefix == "<Table>" || prefix == "")
    {
        if (row.ID >= 2000100 && row.ID < 2002000)
        {
            var number = row.ID % 2000000;
            var element = enumToElement(number / 100);
            var rarity = enumToRarity(number % 10);
            var suffix = number / 10 % 10 == 5 ? " (Extra Weapon Types)" : "";
            prefix = element == null || rarity == null
                ? prefix
                : $"<{rarity} {element}{suffix}>";
        }
        else if (row.ID >= 2100100 && row.ID < 2102000)
        {
            var number = row.ID % 2100000;
            var element = enumToElement(number / 100);
            var rarity = enumToRarity(number % 10);
            var suffix = number / 10 % 10 == 5 ? ", Extra Weapon Types" : "";
            prefix = element == null || rarity == null
                ? prefix
                : $"<{rarity} {element} (Weighted A{suffix})>";
        }
        else if (row.ID >= 2200100 && row.ID < 2202000)
        {
            var number = row.ID % 2200000;
            var element = enumToElement(number / 100);
            var rarity = enumToRarity(number % 10);
            var suffix = number / 10 % 10 == 5 ? ", Extra Weapon Types" : "";
            prefix = element == null || rarity == null
                ? prefix
                : $"<{rarity} {element} (Weighted B{suffix})>";
        }
        else if (row.ID >= 3000000 && row.ID < 4000000)
        {
            var number = row.ID % 300000;
            var rarity = enumToRarity(number % 10);
            var type = enumToElement(number / 100);
            prefix = $"<{rarity} {type} For Class>";
        }
        else if (row.ID >= 7010000 && row.ID < 7110000)
        {
            var number = row.ID % 7000000;
            var element = (number / 10000) switch
            {
                1 => "Frost",
                2 => "Fire",
                3 => "Rot",
                4 => "Magic",
                _ => null
            };
            var rarity = enumToRarity(number % 10);
            var suffix = (number / 10 % 10) switch
            {
                0 => $"{element} Mixed More Elemental",
                1 => $"{element} Mixed Less Elemental",
                2 => $"{element} Mixed More Untyped",
                5 or 7 => $"Untyped/{element}",
                6 => $"Untyped/{element} B",
                _ => null
            };
            prefix = element == null || rarity == null || suffix == null
                ? prefix
                : $"<{rarity} {suffix}>";
        }
        else if (row.ID >= 7900500 && row.ID < 8000000)
        {
            var number = row.ID % 7900000;
            var element = enumToElement(number / 100);
            var bowType = number % 10 == 0 ? "LWB" : "WB";
            prefix = element == null
                ? prefix
                : $"<Uncommon {element} Or Untyped For Class ({bowType})>";
        }
        else if (row.ID >= 10000000)
        {
            var (rarity, type) = enumToRarityAndElement(row.ID % 10000000);
            prefix = $"<{rarity} {(type == null ? "" : $"{type} ")}{weaponTypeForItemTable(row)}>";
        }
    }

    entry.Name = $"{prefix} {referent}".Trim();
}

var attachEffectFmg = fmgsByName["AttachEffectName"];
foreach (var (entry, row) in alignRows("AttachEffectParam"))
{
    if (entry.Name != "") continue;

    var textID = (int)row["attachTextId"].Value;
    if (textID == -1) continue;

    var text = attachEffectFmg[textID];
    if (text == null) continue;

    var prefix = row.ID switch
    {
        < 20000 => "Character Relic",
        >= 310000 and < 400000 => "Talisman",
        >= 6000000 and < 8000000 => "Relic",
        >= 8000000 and < 9000000 => "Weapon Effect",
        >= 9000000 and < 10000000 => "Weapon Power",
        _ => "???",
    };
    entry.Name = $"{prefix}: {text.Replace("\n", " ").Trim()}";
}

foreach (var (entry, row) in alignRows("AttachEffectTableParam"))
{
    var prefix = getTableName(entry.Name) ?? "";
    var referent = paramNames["AttachEffectParam"][(int)row["attachEffectId"].Value].FirstOrDefault();
    if (referent == null) continue;
    var name = referent.Name.Replace("Relic: ", "").Replace("Weapon Effect: ", "").Replace("Weapon Power: ", "");
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

    if (prefix == "<Table>" || prefix == "")
    {
        prefix = row.ID >= 1000000
            ? (row.ID % 1000000) switch
            {
                0 => $"<{weaponTypeForArtsTable(row)}>",
                >= 50 and <= 1500 and var num => $"<{enumToElement(num / 10)} {weaponTypeForArtsTable(row)}>",
                _ => prefix,
            }
            : prefix;
    }

    entry.Name = $"{prefix} {name}".Trim();
}

foreach (var (entry, row) in alignRows("EquipParamCustomWeapon"))
{
    if (entry.Name.StartsWith('[')) continue;

    var referent = paramNames["EquipParamWeapon"][(int)row["targetWeaponId"].Value]
        .FirstOrDefault();
    if (referent == null) continue;

    var prefix = "";
    if (row.ID >= 1000000)
    {
        var mod = row.ID % 10000;
        prefix = mod < 5000
            ? (enumToWeaponAffinity(mod) is string affinity1 ? $"[{affinity1}]" : "")
            : (enumToWeaponAffinity(mod - 5000) is string affinity2 ? $"[Cursed {affinity2}]" : "[Cursed]");
    }
    entry.Name = $"{prefix} {referent.Name}".Trim();
}

foreach (var name in paramsWeCareAbout)
{
    if (!name.Contains("Table")) continue;

    var idsWithMultipleRows = paramDict[name].Rows
        .GroupBy(row => row.ID)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .ToHashSet();
    foreach (var (entry, row) in alignRows(name))
    {
        var weight = row["chanceWeight"].Value;
        var prefix = new Regex("^(<[^>]+>)( |$)").Match(entry.Name).Groups[1]?.Value ?? "";
        if (prefix == "" && idsWithMultipleRows.Contains(row.ID)) prefix = "<Table>";

        if (name switch { "ItemTableParam" => (ushort)weight, _ => (int)weight } == 0)
        {
            entry.Name = $"{prefix} (0% weight)".Trim();
        }
        else if (entry.Name == "")
        {
            entry.Name = prefix;
        }
        else if (!entry.Name.StartsWith(prefix))
        {
            entry.Name = $"{prefix} {entry.Name}";
        }
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

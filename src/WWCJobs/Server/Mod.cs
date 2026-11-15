using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;
using Path = System.IO.Path;

namespace WWCJobs.Server;

#if !WWC   // built into WWC, the mod's metadata is WWC's own (mods/WWC/Server/Wwc.cs)
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "local.wwcjobs.server";
    public string Name { get; init; } = "WWC Jobs";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.3.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new() { ["local.jackal"] = new("^1.0.0") };
    public string? Url { get; init; }
    public string License { get; init; } = "Private";
    public bool HasPrepatcher { get; init; } = false;
}
#endif

/// <summary>Every id comes from fixed text (the job's key and the objective's key), so it never changes once a profile holds it.</summary>
internal static class Ids
{
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wwcjobs|" + key))).ToLowerInvariant()[..24]);
    public static MongoId Quest(string job) => Of("job:" + job.ToLowerInvariant());
    public const string Jackal = "672b4a010000000000000001";
}

/// <summary>config.json: what the player switches. People edit it by hand, so // notes and a trailing comma are fine.</summary>
public sealed class JobsConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    /// <summary>Per item line: true keeps it off The Jackal's shelf until its job is done; false sells it straight away and leaves the job out. A line missing here counts as true.</summary>
    [JsonPropertyName("lockedBehindJobs")] public Dictionary<string, bool> LockedBehindJobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("otherJobsWaitForCompanyBusiness")] public bool OtherJobsWaitForFirst { get; set; } = true;
    [JsonPropertyName("companyBusinessNeedsFirstContact")] public bool FirstNeedsFirstContact { get; set; } = false;
    [JsonPropertyName("levelOffset")] public int LevelOffset { get; set; } = 0;
    [JsonPropertyName("rewardMultiplier")] public double RewardMultiplier { get; set; } = 1.0;
    [JsonPropertyName("standingPerJob")] public double StandingPerJob { get; set; } = 0.01;
    [JsonPropertyName("unlockPicturesPerJob")] public int UnlockPicturesPerJob { get; set; } = 6;

    public bool Locked(string key) => !LockedBehindJobs.TryGetValue(key, out var on) || on;

    public JobsConfig Sane()
    {
        LockedBehindJobs = new Dictionary<string, bool>(LockedBehindJobs ?? [], StringComparer.OrdinalIgnoreCase);
        LevelOffset = Math.Clamp(LevelOffset, -70, 70);
        RewardMultiplier = double.IsFinite(RewardMultiplier) ? Math.Clamp(RewardMultiplier, 0, 100) : 1.0;
        StandingPerJob = double.IsFinite(StandingPerJob) ? Math.Clamp(StandingPerJob, 0, 1) : 0.01;
        UnlockPicturesPerJob = Math.Clamp(UnlockPicturesPerJob, 0, 30);
        return this;
    }
}

/// <summary>jobs.json: the job chains. Content, not settings; a new item line is a new entry here.</summary>
public sealed class JobFile
{
    [JsonPropertyName("lines")] public List<JobLine> Lines { get; set; } = [];
}

/// <summary>One WWC item line and its chain of jobs, done in order. The first line's first job (Company Business) is the one every other chain waits for.</summary>
public sealed class JobLine
{
    /// <summary>Stable key, also the config.json switch. Quest ids are made from it and the job's own key.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("line")] public string Name { get; set; } = "";
    /// <summary>Template _name prefixes (case ignored) of the Jackal offers this line locks, e.g. "wwcmag_".</summary>
    [JsonPropertyName("namePrefixes")] public List<string> NamePrefixes { get; set; } = [];
    [JsonPropertyName("jobs")] public List<Job> Jobs { get; set; } = [];
}

public sealed class Job
{
    /// <summary>Stable key inside its line: the quest id is made from "line/key". Never rename a key once a profile holds the job.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonIgnore] public string Id { get; set; } = "";
    [JsonIgnore] public string Line { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("map")] public string Map { get; set; } = "any";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "596b465486f77457ca186188.jpg";
    [JsonPropertyName("level")] public int Level { get; set; } = 1;
    [JsonPropertyName("xp")] public int Xp { get; set; }
    [JsonPropertyName("roubles")] public int Roubles { get; set; }
    /// <summary>Which of the line's offers this job opens: template _name prefixes, or "=name" for one exact name. The longest match wins;
    /// offers no job claims open with the chain's last job.</summary>
    [JsonPropertyName("opens")] public List<string> Opens { get; set; } = [];
    /// <summary>"line/job" keys of jobs in other chains that must be finished first. A job that is left out is ignored.</summary>
    [JsonPropertyName("requires")] public List<string> Requires { get; set; } = [];
    [JsonPropertyName("unlocks")] public string Unlocks { get; set; } = "";
    [JsonPropertyName("story")] public string Story { get; set; } = "";
    [JsonPropertyName("started")] public string Started { get; set; } = "";
    [JsonPropertyName("done")] public string Done { get; set; } = "";
    [JsonPropertyName("objectives")] public List<Objective> Objectives { get; set; } = [];
}

public sealed class Objective
{
    /// <summary>Stable key inside the job: the condition id is made from it.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    /// <summary>handover, kill, visit, mark, stash, extract, loyalty (The Jackal's loyalty level), skill.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    /// <summary>Item templates; "parent:&lt;id&gt;" stands for every item of that class (custom ones too).</summary>
    [JsonPropertyName("items")] public List<string> Items { get; set; } = [];
    [JsonPropertyName("count")] public int Count { get; set; } = 1;
    [JsonPropertyName("foundInRaid")] public bool FoundInRaid { get; set; }
    /// <summary>Kill target: Savage, AnyPmc or Any.</summary>
    [JsonPropertyName("target")] public string Target { get; set; } = "Any";
    [JsonPropertyName("maps")] public List<string> Maps { get; set; } = [];
    [JsonPropertyName("calibers")] public List<string> Calibers { get; set; } = [];
    [JsonPropertyName("bodyParts")] public List<string> BodyParts { get; set; } = [];
    /// <summary>Kill while wearing any one of these (items or "parent:" entries).</summary>
    [JsonPropertyName("wearing")] public List<string> Wearing { get; set; } = [];
    /// <summary>Kill: bot roles (bossKilla, pmcBot for Raiders, exUsec for Rogues, sectantWarrior ...); the target is then Savage.</summary>
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = [];
    /// <summary>Kill: the weapons that count (items or "parent:" entries).</summary>
    [JsonPropertyName("weapons")] public List<string> Weapons { get; set; } = [];
    /// <summary>Kill: in-raid hours [from, to], e.g. [22, 5].</summary>
    [JsonPropertyName("daytime")] public List<int> Daytime { get; set; } = [];
    /// <summary>Kill: at least this many metres away.</summary>
    [JsonPropertyName("distance")] public int Distance { get; set; }
    /// <summary>loyalty: the level; skill: the level, with "skill" naming it (e.g. Charisma).</summary>
    [JsonPropertyName("value")] public int Value { get; set; }
    [JsonPropertyName("skill")] public string Skill { get; set; } = "";
    [JsonPropertyName("zone")] public string Zone { get; set; } = "";
    [JsonPropertyName("seconds")] public int Seconds { get; set; } = 10;
    /// <summary>stash: true mails the item when the job is accepted.</summary>
    [JsonPropertyName("given")] public bool Given { get; set; }
    [JsonPropertyName("exit")] public string? Exit { get; set; }
}

internal static class Files
{
    public static readonly JsonSerializerOptions Forgiving = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static T? Read<T>(string path, Action<string> error) where T : class
    {
        if (!File.Exists(path)) { error($"{Path.GetFileName(path)} is missing"); return null; }
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Forgiving); }
        catch (JsonException ex) { error($"{Path.GetFileName(path)} has a mistake near line {(ex.LineNumber ?? 0) + 1}: {ex.Message}"); return null; }
        catch (Exception ex) { error($"{Path.GetFileName(path)} could not be read: {ex.Message}"); return null; }
    }
}

/// <summary>
/// Runs after The Jackal (TraderRegistration + 100), the Black Division story (+ 125), Jackal Origin (+ 130) and every WWC line's offers (+ 132 to + 150).
/// For each job: finds the Jackal offers whose template name starts with one of its prefixes, adds the job as a Jackal quest, and lists those offers
/// in his quest assort under "success", so SPT hides them from a character until the job is finished. Nothing in the item mods is changed.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 200)]
public class JobsLoader(ISptLogger<JobsLoader> logger, ModHelper modHelper, CustomQuestService questService, LocaleTable locales,
    TradersTable traders, TemplateTable templates) : IOnLoad
{
    private const string Tag = "[WWC Jobs]";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { logger.Error($"{Tag} stopped early, every line stays on sale as before: {ex}"); }
        return Task.CompletedTask;
    }

    private void Run()
    {
        var dir = ModFolder.Of(modHelper);
        var cfg = Files.Read<JobsConfig>(Path.Combine(dir, "config.json"), m => logger.Error($"{Tag} {m}; using the normal settings (every line waits for its job)"))?.Sane() ?? new JobsConfig().Sane();
        if (!cfg.ModEnabled) { logger.Info($"{Tag} switched off in config.json; The Jackal sells every line straight away"); return; }
        var file = Files.Read<JobFile>(Path.Combine(dir, "jobs.json"), m => logger.Error($"{Tag} {m}; no jobs added, every line stays on sale"));
        if (file is null || file.Lines.Count == 0) return;

        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Base is null || jackal.Assort?.Items is null) { logger.Warning($"{Tag} The Jackal is not installed; no jobs added"); return; }
        var assort = jackal.Assort;
        var questAssort = jackal.QuestAssort;
        foreach (var k in new[] { "started", "success", "fail" }) if (!questAssort.ContainsKey(k)) questAssort[k] = new Dictionary<MongoId, MongoId>();
        var alreadyLocked = questAssort.Values.SelectMany(d => d.Keys).ToHashSet();
        string NameOf(Item offer) => templates.Items.TryGetValue(offer.Template, out var t) ? t.Name ?? "" : "";

        // Root offers by line (an offer belongs to the first line whose prefix matches its template's _name), then by job inside the line.
        var roots = assort.Items.Where(i => i.ParentId == "hideout").ToList();
        var claimed = new HashSet<MongoId>();
        var lineKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<(JobLine Line, List<Item> Offers)>();
        foreach (var line in file.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Key) || !lineKeys.Add(line.Key)) { logger.Error($"{Tag} a line in jobs.json has no key or a repeated key ({line.Key}); skipped"); continue; }
            var prefixes = (line.NamePrefixes ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            var offers = roots.Where(r => !claimed.Contains(r.Id) && prefixes.Any(p => NameOf(r).StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var r in offers) claimed.Add(r.Id);
            lines.Add((line, offers));
        }

        // Which chains go in. A chain is left out when its line is switched off, when The Jackal sells nothing from it, or when any of its jobs is broken.
        var report = new List<string>();
        var keep = new List<(JobLine Line, List<(Job Job, List<Item> Offers)> Jobs)>();
        foreach (var (line, offers) in lines)
        {
            if (!cfg.Locked(line.Key)) { report.Add($"{line.Name}: sold straight away (switched off in config.json)"); continue; }
            if (offers.Count == 0) { report.Add($"{line.Name}: The Jackal sells none of it, so its {line.Jobs.Count} job(s) are left out"); continue; }
            var jobKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jobs = (line.Jobs ?? []).ToList();
            if (jobs.Count == 0 || jobs.Any(j => string.IsNullOrWhiteSpace(j.Key) || !jobKeys.Add(j.Key)))
            { logger.Error($"{Tag} {line.Name}: a job has no key or a repeated key, so the line stays on sale"); continue; }
            for (var i = 0; i < jobs.Count; i++) { jobs[i].Id = $"{line.Key}/{jobs[i].Key}"; jobs[i].Line = line.Name; }
            // Each offer opens with the job whose "opens" pattern fits it best ("=name" exact beats any prefix, a longer prefix beats a shorter one); the rest with the last job.
            var byJob = jobs.Select(j => (Job: j, Offers: new List<Item>())).ToList();
            foreach (var offer in offers)
            {
                var name = NameOf(offer);
                int best = -1, score = 0;
                for (var i = 0; i < jobs.Count; i++)
                    foreach (var p in jobs[i].Opens ?? [])
                    {
                        var s = p.StartsWith('=') ? (name.Equals(p[1..], StringComparison.OrdinalIgnoreCase) ? 10_000 : 0)
                                                  : (p.Length > 0 && name.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? p.Length : 0);
                        if (s > score) { score = s; best = i; }
                    }
                byJob[best < 0 ? jobs.Count - 1 : best].Offers.Add(offer);
            }
            keep.Add((line, byJob));
        }
        var added = keep.SelectMany(l => l.Jobs).Select(j => j.Job.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var first = keep.Count > 0 && keep[0].Line == lines.FirstOrDefault().Line ? keep[0].Jobs[0].Job : null;
        var firstContact = cfg.FirstNeedsFirstContact ? FindFirstContact() : null;
        if (cfg.FirstNeedsFirstContact && firstContact is null) logger.Warning($"{Tag} First Contact not found; Company Business starts without it");

        var langs = locales.Global.Keys.Append("en").Distinct(StringComparer.Ordinal).ToArray();
        int locked = 0, made = 0;
        foreach (var (line, jobs) in keep)
        {
            // Build the whole chain first: if one job can't be built the line stays on sale rather than ending in a job nobody can reach.
            var builtChain = new List<(Built Built, List<Item> Offers)>();
            try
            {
                for (var i = 0; i < jobs.Count; i++)
                {
                    var (job, offers) = jobs[i];
                    var requires = new List<MongoId>();
                    if (i > 0) requires.Add(Ids.Quest(jobs[i - 1].Job.Id));
                    else if (cfg.OtherJobsWaitForFirst && first is not null && job != first) requires.Add(Ids.Quest(first.Id));
                    if (firstContact is not null && job == first) requires.Add(firstContact.Id);
                    requires.AddRange((job.Requires ?? []).Where(r => added.Contains(r) && !r.Equals(job.Id, StringComparison.OrdinalIgnoreCase)).Select(Ids.Quest));
                    var unlocks = offers.Select(o => (o.Id, o.Template, Level: assort.LoyalLevelItems?.TryGetValue(o.Id, out var ll) == true ? ll : 1)).ToList();
                    builtChain.Add((JobBuilder.Build(job, i + 1, jobs.Count, cfg, requires, unlocks, templates.Items), offers));
                }
            }
            catch (Exception ex) { logger.Error($"{Tag} {line.Name}: a job could not be built, so the line stays on sale: {ex.Message}"); continue; }

            var steps = new List<string>();
            foreach (var (built, offers) in builtChain)
            {
                foreach (var w in built.Warnings) logger.Warning($"{Tag} \"{built.Quest.QuestName}\": {w}");
                var result = questService.CreateQuest(new NewQuestDetails
                {
                    NewQuest = built.Quest,
                    Locales = langs.ToDictionary(l => l, _ => new Dictionary<string, string>(built.Text)),
                    LockedToSide = null,
                });
                if (!result.Success) { logger.Error($"{Tag} \"{built.Quest.QuestName}\" not added: {string.Join(", ", result.Errors)}"); continue; }
                made++;
                int n = 0, skipped = 0;
                foreach (var offer in offers)
                {
                    if (alreadyLocked.Contains(offer.Id)) { skipped++; continue; }
                    questAssort["success"][offer.Id] = built.Quest.Id;
                    n++;
                }
                locked += n;
                steps.Add($"{built.Quest.QuestName} (lvl {built.Level}{(n > 0 ? $", opens {n}" : "")}{(skipped > 0 ? $", {skipped} locked elsewhere" : "")})");
            }
            report.Add($"{line.Name}: {string.Join(" > ", steps)}");
        }

        foreach (var line in report) logger.Info($"{Tag} {line}");
        logger.Success($"{Tag} {made} job(s) in {keep.Count} chain(s) at The Jackal, {locked} offer(s) wait for them");
    }

    /// <summary>The Jackal mod's first campaign job: by name, else his campaign quest (its own icon) that waits on no other quest.</summary>
    private Quest? FindFirstContact()
    {
        var mine = templates.Quests.Values.Where(q => q.TraderId == new MongoId(Ids.Jackal)).ToList();
        return mine.FirstOrDefault(q => q.QuestName == "First Contact")
            ?? mine.FirstOrDefault(q => q.Image == "/files/quest/icon/596b465486f77457ca186188.jpg" && (q.Conditions.AvailableForStart ?? []).All(c => c.ConditionType != "Quest"));
    }
}

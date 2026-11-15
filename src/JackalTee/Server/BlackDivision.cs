using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils.Json;

namespace JackalTee.Server;

/// <summary>The Black Division story for The Jackal: a mix of find-and-mark jobs, document hand-ins and fetch quests. The last one opens the crew shirts for sale. Sites are the game's own marker spots, so no client mod is needed.</summary>
internal static class BlackDivision
{
    public const int QuestCount = 10;
    private const string Marker = "5991b51486f77447b112d44f";     // MS2000 marker
    private const string Roubles = "5449016a4bdc2d6f028b456f";
    private const string Any = "any", Customs = "56f40101d2720b2a4d8b45d6", Woods = "5704e3c2d2720bac5b8b4567", Shoreline = "5704e554d2720bac5b8b456e",
        Reserve = "5704e5fad2720bc05b8b4567", Lighthouse = "5704e4dad2720bb55b8b4567", Streets = "5714dc692459777137212e12";

    public static MongoId QuestId(int n) => Ids.Of($"bd|quest:{n}");
    private static MongoId Id(string key) => Ids.Of("bd|" + key);

    private sealed record Site(string Zone, int PlantSeconds, string Text);
    private sealed record Fetch(string Item, int Count, string Name);
    private sealed record Job(string Title, string Map, string Icon, int Level, int Xp, int Money, string Story, string Start, string Done, Site[] Sites, Fetch[] Items);

    private static readonly Job[] Jobs =
    [
        new("Unsigned Freight", Customs, "6762fe1e6470bf1c0f048b96", 15, 6000, 40000,
            "Got something for you, if you're not busy getting shot at. Old contact of mine came back around. The one with the bronze lion on his desk, you won't know him. He asked for you by name, which I didn't love, and he paid up front, which he has never done in his life.\n\nThere are three crates sitting on Customs that nobody ever signed for. Rifles, no serial numbers worth reading. He wants a marker on each one. When I asked who they were for, he said two words. Black Division.\n\nYeah, I know. Men with no patches who creep around TerraGroup sites at night and walk off with the data. USEC says it's a campfire story, BEAR says the same, and both of them will tell you nobody could feed a fighting unit through the cordon anyway. Maybe so. His money's real though.\n\nMarkers will be in your messages. Don't lose them, I'm not made of markers.",
            "Markers are in your messages. Three crates, three markers, no creative accounting.",
            "All three came through on my end and he paid inside the hour. Didn't ask where they were, either. Makes you wonder why he needed you to go and look.",
            [new("TerragroupBOX_1", 15, "Locate and mark the first unsigned weapons crate with an MS2000 Marker on Customs"),
             new("TerragroupBOX_2", 15, "Locate and mark the second unsigned weapons crate with an MS2000 Marker on Customs"),
             new("TerragroupBOX_3", 15, "Locate and mark the third unsigned weapons crate with an MS2000 Marker on Customs")], []),
        new("Manifest", Any, "597a0fad86f77426d464f995", 16, 6500, 45000,
            "He's back already. Says those crates had paperwork, and he'd like it before somebody who can read gets to it. Apparently the service crews wrote the real serial numbers in their Tech manuals and the drivers kept the true counts in their diaries, because of course they did. Nobody trusts the official book.\n\nSo, one Tech manual and one Diary. Found in raid, not off some guy on the flea. He says he'll be able to tell. I don't know how, and I'm not asking.",
            "One manual, one diary. And no, the one you wrote in secondary school doesn't count.",
            "He read them both twice, then burned them in my sink. My sink. I've stopped trying to work out what this man wants.",
            [], [new("590c639286f774151567fa95", 1, "Tech manual"), new("590c645c86f77412b01304d9", 1, "Diary")]),
        new("Back at the Bench", Any, "596b465486f77457ca186188", 18, 7500, 55000,
            "Now he wants workshop stock, if you can believe it. Three lots of weapon parts. Two tins of Kite powder. Some WD-40. All found in raid, because he says shelf stock is garbage and honest parts come out of honest raids. His words.\n\nLook, nobody talks like that about gun oil unless they've done it for a living. I've got my own ideas about what he used to be. You keep yours to yourself and I'll keep mine.",
            "Three weapon parts, two Kite, one WD-40. Try not to set fire to the powder on your way back.",
            "Went out the same day. He sent me back one cartridge in an envelope with a note that said \"still true.\" That's it. That's the whole note. Strange man.",
            [], [new("5d1c819a86f774771b0acd6c", 3, "Weapon parts"), new("590c5a7286f7747884343aea", 2, "Gunpowder \"Kite\""), new("590c5bbd86f774785762df04", 1, "WD-40 (100ml)")]),
        new("Written Off", Reserve, "5a29221d86f77457303da9fa", 20, 9000, 65000,
            "Reserve this time. There are three BMP-2s out there that have been listed as destroyed for years. Funny thing about that, they're not. Someone went through them on the quiet, pulled the ammo and the good parts, and fed the lot to a unit that, officially, isn't here.\n\nHe wants them marked. Says when someone finally comes looking, the paperwork's the first thing that falls apart. He sounded pleased about it, which worried me more than anything else he's said.",
            "Three BMPs. Markers are in your messages. Watch yourself around those barracks.",
            "He asked me if the guns were still mounted. I said I'd ask you. Then I decided I'd rather not know, so don't tell me.",
            [new("tadeush_bmp2_area_mark_2", 10, "Find and mark the first written-off BMP-2 with an MS2000 Marker on Reserve"),
             new("tadeush_bmp2_area_mark_11", 10, "Find and mark the second written-off BMP-2 with an MS2000 Marker on Reserve"),
             new("tadeush_bmp2_area_mark_12", 10, "Find and mark the third written-off BMP-2 with an MS2000 Marker on Reserve")], []),
        new("Holes in the Ground", Woods, "59675e7b86f77414b25fb049", 22, 10000, 75000,
            "Woods. There are four bunkers out there the maps only half admit to. He reckons they were stocked with weapons long after the cordon went up, back when nothing was meant to be getting in or out. Mark all four.\n\nThere was a storeman out there who wrote everything down, too, so keep an eye out for a Slim diary. Found in raid, same as before.\n\nI'll be straight with you. I got curious and started asking around about who I'm working for. He must have heard, because this turned up the next morning. A photo. Squad in black kit, no patches, no names, no flag on anybody, standing in one of those bunkers. Guy at the front is looking right at the camera, and it's him. So that's fun.",
            "Four bunkers and a diary. And if you see anyone in black with no patches, I'd suggest being somewhere else.",
            "Well. He knows I know. He wants to talk to you himself on the next job. I told him you don't do small talk. He laughed, which I didn't know he could do.",
            [new("meh_45_radio_area_mark_1", 10, "Locate and mark the first weapons bunker with an MS2000 Marker on Woods"),
             new("meh_45_radio_area_mark_2", 10, "Locate and mark the second weapons bunker with an MS2000 Marker on Woods"),
             new("meh_45_radio_area_mark_3", 10, "Locate and mark the third weapons bunker with an MS2000 Marker on Woods"),
             new("meh_45_radio_area_mark_4", 10, "Locate and mark the fourth weapons bunker with an MS2000 Marker on Woods")],
            [new("590c651286f7741e566b6461", 1, "Slim diary")]),
        new("Recovered Property", Any, "63a94151655ec5555b4aa9af", 24, 12000, 90000,
            "He wants you on the radio himself, so I'm handing this over. Don't interrupt him, he hates that.\n\nIs this on? Right. Hello. You've been doing good work and Jackal says you've earned a straight answer, so here it is. I was the armourer for Black Division. Yes, we're real. No, USEC and BEAR never believed it, and I'll be honest, that was rather the point. One-word names, no markings, and not a single rifle that ever showed up on any army's books. Getting those rifles through the cordon and keeping them running was my whole life for a long time.\n\nThe Western guns you're seeing out there now, a fair few of them are mine, or somebody's bad copy of mine. Bring me a Colt M4A1 and an HK MP5, found in raid. Don't clean them up first. I want to see what the years have done to them.",
            "M4 and an MP5. As you find them, mind. I can tell when someone's been at a gun with a rag.",
            "Ha. Look at the state of this extractor. Worn to nothing and the thing still runs. That's more than I can say for most of the people who carried them.",
            [], [new("5447a9cd4bdc2dbd208b4567", 1, "Colt M4A1"), new("5926bb2186f7744b1c6c6e60", 1, "HK MP5 Navy")]),
        new("Back of the Armoury", Any, "596b465486f77457ca186188", 26, 14000, 100000,
            "It's me again. This one's going to sound daft, so just hear me out.\n\nThe guns kept them alive, but the shirts kept them sane. On a job nobody wore so much as a patch. So on the days between, when they were just sat around cleaning rifles and getting on each other's nerves, I ran a little press in the back of the armoury and printed shirts for them. Different design for every crew. Some of them were terrible. They loved them anyway. You'd be amazed what a stupid shirt does for a man who hasn't slept.\n\nI want to get that press going one more time. Two lots each of Ripstop, Fleece and Printer paper, found in raid. The paper's for test pulls, before you ask.",
            "Two Ripstop, two Fleece, two Printer paper. Yes, I know what it sounds like. Go on.",
            "It runs! First pull came out crooked, mind, just like the old days. I may have got a bit sentimental. Don't tell Jackal.",
            [], [new("5e2af4a786f7746d3f3c3400", 2, "Ripstop fabric"), new("5e2af47786f7746d404f3aaa", 2, "Fleece fabric"), new("577e1c9d2459773cd707c525", 2, "Printer paper")]),
        new("Heavy on the Way Out", Lighthouse, "61abcd4c3b7a0b53f515c5e6", 28, 16000, 115000,
            "It's me. Lighthouse next, and this one matters.\n\nYou want to know how you move a fighting unit past a cordon everyone swears can't be crossed? You do it at night, by helicopter, and you pay somebody to look at the sea. Mine came in so heavy with guns the pilot used to complain about it. The cargo they left behind is still sitting there with TerraGroup's name on the side.\n\nMark the helicopter, mark the cargo. And every one of those flights got recorded, because somebody always records it, so I want a Secure magnetic tape cassette, found in raid. Nobody ever thinks to burn the tape.",
            "Helicopter, cargo, tape. And keep your head down out there, that lot have long memories.",
            "You'll have noticed nobody came to stop you. I'd love to tell you that's because nobody's watching. Let's just get this finished, yes?",
            [new("qlight_pr1_heli1_mark", 30, "Mark the arms helicopter with an MS2000 Marker on Lighthouse"),
             new("qlight16_peace_terra", 30, "Locate and mark the TerraGroup cargo with an MS2000 Marker on Lighthouse")],
            [new("61bf7c024770ee6f9c6b8b53", 1, "Secure magnetic tape cassette")]),
        new("Last Shipment", Streets, "63a90fd7c31b00242d28a92e", 31, 18000, 130000,
            "Nearly there now. Streets of Tarkov. Last shipment I ever moved for them, staged right there in the city on its way down to the port at Terminal. Mark the cargo area, and mark the Stryker that sat guarding it.\n\nAnd find me some TerraGroup Blue Folders, found in raid. That's what all the night work was really for, you know. Not the shooting. The data we pulled out of their sites. Those folders are the only paperwork that ever had our client's name on it, and I've spent years pretending I didn't know that.\n\nOne more after this. Then we're square, you and me, and I'll pay what I owe.",
            "Cargo area, Stryker, folders. Nearly done now. Don't get careless on me.",
            "That's every crate I ever moved, then. All but the last one. Give me a day. I need to think about how to say this one.",
            [new("quest_zone_keeper10_place", 40, "Mark the Division's cargo area with an MS2000 Marker on Streets of Tarkov"),
             new("quest_zone_place_c14_revx_2", 40, "Locate and mark the Stryker with an MS2000 Marker on Streets of Tarkov")],
            [new("6389c8c5dbfd5e4b95197e6b", 1, "TerraGroup \"Blue Folders\" materials")]),
        new("Paid in Full", Shoreline, "5a27c50f86f7740b3d65e16a", 33, 25000, 200000,
            "Alright. Last one, and I'll ask you to do it properly.\n\nThere's a helicopter at the Health Resort on Shoreline. That's where The Wedge finished with me. I was meant to be on it when it left, and then, after a bit of a conversation, I was meant to be under it. I got out the back way, on my stomach, and it took most of a night. Mark the helicopter. Mark the way I crawled out. I'd like it on the record somewhere that I did.\n\nBring me two Intelligence folders as well, found in raid. Those are the last orders I ever got from them, or near enough.\n\nNow, the box. Jackal's been sitting on a box from the press all this time, whatever's left of the run. Finish this and I'll tell him to open it for you. He'll charge you, he charges everybody, I've known him a long time. But nobody else gets to buy them. Wear one where the Division can see it and they'll know exactly who sent you. I'd quite like that.",
            "Take your time with this one. It's waited this long.",
            "That's it, then. We're square. Go and see Jackal, the box is open and they're on his rack. And if anybody asks where you got the shirt, you tell them a dead man paid his debts. Thank you. I mean that.",
            [new("place_peacemaker_008_2_N1", 30, "Locate and mark the helicopter at the Health Resort with an MS2000 Marker on Shoreline"),
             new("place_peacemaker_008_2_N2", 30, "Mark the Armourer's way out with an MS2000 Marker on Shoreline")],
            [new("5c12613b86f7743bbe2c3f76", 2, "Intelligence folders")]),
    ];

    public static IEnumerable<NewQuestDetails> Build(MongoId trader, IEnumerable<string> languages)
    {
        var langs = languages.Append("en").Distinct(StringComparer.Ordinal).ToArray();
        for (var i = 0; i < Jobs.Length; i++)
        {
            var job = Jobs[i];
            var n = i + 1;
            var id = QuestId(n);
            var key = id.ToString();
            var text = new Dictionary<string, string>
            {
                [$"{key} name"] = job.Title,
                [$"{key} description"] = job.Story,
                [$"{key} note"] = $"Black Division {n}/{QuestCount}.",
                [$"{key} startedMessageText"] = job.Start,
                [$"{key} successMessageText"] = job.Done,
                [$"{key} failMessageText"] = "Doesn't matter. It'll still be there when you're ready.",
                [$"{key} changeQuestMessageText"] = "Something's changed. Have another look at the job.",
                [$"{key} acceptPlayerMessage"] = "Alright, I'm on it.",
                [$"{key} completePlayerMessage"] = "Done. It's all there.",
                [$"{key} declinePlayerMessage"] = "Not right now.",
            };

            var finish = new List<QuestCondition>();
            for (var s = 0; s < job.Sites.Length; s++)
            {
                var site = job.Sites[s];
                var condition = new QuestCondition
                {
                    Id = Id($"site:{n}:{s}"), ConditionType = "PlaceBeacon", Index = s, Value = 1, PlantTime = site.PlantSeconds, ZoneId = site.Zone,
                    Target = new ListOrT<string>([Marker], null!), DynamicLocale = false, ParentId = "", GlobalQuestCounterId = "", VisibilityConditions = [],
                };
                finish.Add(condition);
                text[condition.Id.ToString()] = site.Text;
            }
            for (var f = 0; f < job.Items.Length; f++)
            {
                var item = job.Items[f];
                var handover = new QuestCondition
                {
                    Id = Id($"fetch:{n}:{f}"), ConditionType = "HandoverItem", Index = job.Sites.Length + f, Value = item.Count, Target = new ListOrT<string>([item.Item], null!),
                    DogtagLevel = 0, OnlyFoundInRaid = true, MinDurability = 0, MaxDurability = 100, IsEncoded = false, DynamicLocale = false, ParentId = "", GlobalQuestCounterId = "", VisibilityConditions = [],
                };
                finish.Add(handover);
                text[handover.Id.ToString()] = $"Hand over {item.Count} found in raid {item.Name}";
            }

            var start = new List<QuestCondition>
            {
                new() { Id = Id($"level:{n}"), ConditionType = "Level", CompareMethod = ">=", Value = job.Level, Index = 0, DynamicLocale = false, ParentId = "", GlobalQuestCounterId = "", VisibilityConditions = [] },
            };
            if (n > 1)
            {
                start.Add(new()
                {
                    Id = Id($"previous:{n}"), ConditionType = "Quest", Index = 1, Target = new ListOrT<string>(null!, QuestId(n - 1).ToString()), Status = [QuestStatusEnum.Success],
                    AvailableAfter = 0, Dispersion = 0, DynamicLocale = false, ParentId = "", GlobalQuestCounterId = "", VisibilityConditions = [],
                });
            }

            var started = new List<Reward>();
            if (job.Sites.Length > 0)
            {
                var markers = Enumerable.Range(0, job.Sites.Length).Select(m => new Item { Id = Id($"marker:{n}:{m}"), Template = new MongoId(Marker), Upd = new() { StackObjectsCount = 1 } }).ToList();
                started.Add(Common(Id($"markers:{n}"), RewardType.Item, job.Sites.Length, markers[0].Id.ToString()) with { FindInRaid = false, IsEncoded = false, Items = markers });
            }
            var moneyItem = Id($"money-item:{n}");
            var success = new List<Reward>
            {
                Common(Id($"xp:{n}"), RewardType.Experience, job.Xp),
                Common(Id($"standing:{n}"), RewardType.TraderStanding, 0.03, trader.ToString()),
                Common(Id($"money:{n}"), RewardType.Item, job.Money, moneyItem.ToString()) with
                {
                    FindInRaid = false, IsEncoded = false,
                    Items = [new() { Id = moneyItem, Template = new MongoId(Roubles), Upd = new() { StackObjectsCount = job.Money } }],
                },
            };
            var quest = new Quest
            {
                Id = id, QuestName = job.Title, TraderId = trader,
                Name = $"{key} name", Description = $"{key} description", Note = $"{key} note",
                AcceptPlayerMessage = $"{key} acceptPlayerMessage", DeclinePlayerMessage = $"{key} declinePlayerMessage", CompletePlayerMessage = $"{key} completePlayerMessage",
                StartedMessageText = $"{key} startedMessageText", SuccessMessageText = $"{key} successMessageText", FailMessageText = $"{key} failMessageText", ChangeQuestMessageText = $"{key} changeQuestMessageText",
                CanShowNotificationsInGame = true, AcceptanceAndFinishingSource = "eft", ProgressSource = "eft", Side = "Pmc", Location = job.Map,
                Type = job.Sites.Length > 0 ? QuestTypeEnum.Exploration : QuestTypeEnum.Completion, Image = $"/files/quest/icon/{job.Icon}.jpg", Status = 0,
                IsKey = false, KeyQuest = false, SecretQuest = false, Restartable = false, InstantComplete = false, ArenaLocations = [], RankingModes = [], GameModes = [],
                Conditions = new() { AvailableForStart = start, AvailableForFinish = finish, Fail = [] },
                Rewards = new()
                {
                    ["Started"] = started,
                    ["Fail"] = [],
                    ["Success"] = success,
                },
            };
            yield return new NewQuestDetails { NewQuest = quest, Locales = langs.ToDictionary(l => l, _ => new Dictionary<string, string>(text)), LockedToSide = null };
        }
    }

    private static Reward Common(MongoId id, RewardType type, double value, string? target = null) => new()
    {
        Id = id, Type = type, Value = value, Target = target, IsHidden = false, Unknown = false, AvailableInGameEditions = [], GameMode = ["regular", "pve"],
    };
}

/// <summary>After The Jackal has registered himself: adds the questline. Finishing it opens the shirts for sale (see the offers in Mod.cs).</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 125)]
public class TeeStory(ISptLogger<TeeStory> logger, ModHelper modHelper, CustomQuestService quests, LocaleTable locales) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        var cfg = modHelper.GetJsonDataFromFile<TeeConfig>(dir, "config.json") ?? new TeeConfig();
        if (!cfg.Enabled || !cfg.QuestRequired) { logger.Info("[JackalTee] Black Division story is off in config.json"); return Task.CompletedTask; }
        var added = 0;
        foreach (var quest in BlackDivision.Build(new MongoId(Ids.Jackal), locales.Global.Keys))
        {
            var result = quests.CreateQuest(quest);
            if (!result.Success) { logger.Error($"[JackalTee] story quest {quest.NewQuest?.QuestName} not added: {string.Join(", ", result.Errors)}"); continue; }
            added++;
        }
        logger.Success($"[JackalTee] Black Division story: {added} of {BlackDivision.QuestCount} quests, the last one opens the shirts for sale");
        return Task.CompletedTask;
    }
}

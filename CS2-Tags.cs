using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace CS2_Tags;

[MinimumApiVersion(159)]
public class CS2_Tags : BasePlugin, IPluginConfig<CS2_TagsConfig>
{
    private HashSet<string> GaggedIds = new HashSet<string>();
    private ConcurrentDictionary<string, string> PlayerAssignedFlags = new ConcurrentDictionary<string, string>();
    public static JObject? JsonTags { get; private set; }
    public static JArray? OrderedFlags { get; private set; }
    
    public CS2_TagsConfig Config { get; set; } = new CS2_TagsConfig();

    public override string ModuleName => "CS2-Tags";
    public override string ModuleDescription => "Add player tags easily in cs2 game via API";
    public override string ModuleAuthor => "daffyy, extended";
    public override string ModuleVersion => "1.1.0";

    private HttpClient httpClient = new HttpClient();
    private CounterStrikeSharp.API.Modules.Timers.Timer? updateTimer;

    public void OnConfigParsed(CS2_TagsConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        httpClient.Timeout = TimeSpan.FromSeconds(10); // Evitar travamentos longos
        
        // Tentar carregar backup caso a API falhe na inicialização
        LoadJsonBackup(ModuleDirectory + "/tags.json");

        // Buscar da API imediatamente
        _ = FetchTagsFromApi();

        // Buscar tags dos jogadores online agora (ex: hot reload)
        AddTimer(1.0f, () => {
            var onlineSids = Utilities.GetPlayers()
                .Where(p => p.IsValid && !p.IsBot && p.AuthorizedSteamID != null)
                .Select(p => p.AuthorizedSteamID!.SteamId64.ToString())
                .ToList();
            
            if (onlineSids.Count > 0)
            {
                _ = FetchPlayersTags(onlineSids);
            }
        });

        Server.PrintToConsole($"[CS2-Tags] Registered with UpdateInterval: {Config.UpdateIntervalSeconds}s");

        // Agendar atualizações autoáticas
        if (Config.UpdateIntervalSeconds > 0)
        {
            updateTimer = AddTimer(Config.UpdateIntervalSeconds, () =>
            {
                _ = FetchTagsFromApi();
                // Re-fetch para todos os jogadores online em batch
                var onlineSids = Utilities.GetPlayers()
                    .Where(p => p.IsValid && !p.IsBot && p.AuthorizedSteamID != null)
                    .Select(p => p.AuthorizedSteamID!.SteamId64.ToString())
                    .ToList();
                
                if (onlineSids.Count > 0)
                {
                    _ = FetchPlayersTags(onlineSids);
                }
            }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        }

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        
        AddCommandListener("say", OnPlayerChat);
        AddCommandListener("say_team", OnPlayerChatTeam);
    }

    public override void Unload(bool hotReload)
    {
        updateTimer?.Kill();
        httpClient.Dispose();
        base.Unload(hotReload);
    }

    private async Task FetchTagsFromApi()
    {
        try
        {
            string url = $"{Config.ApiUrl.TrimEnd('/')}/perms/list";
            HttpResponseMessage response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            string jsonResponse = await response.Content.ReadAsStringAsync();
            JObject apiData = JObject.Parse(jsonResponse);

            if (apiData["success"]?.Value<bool>() != true)
            {
                Server.NextFrame(() => Server.PrintToConsole("[CS2-Tags] [API] Failed to fetch tags: API returned success=false."));
                return;
            }

            JArray rolesArray = (JArray)apiData["data"]!;
            
            // Construir a nova estrutura de tags em memória compatível com a original
            JObject newTagsStructure = new JObject();
            JObject tagsRoot = new JObject();

            foreach (var token in rolesArray)
            {
                if (token is not JObject role) continue;

                string? flag = role["flag"]?.ToString();
                string? prefix = role["discordname"]?.ToString();
                string? hexColor = role["color"]?.ToString();

                if (string.IsNullOrEmpty(flag) || string.IsNullOrEmpty(prefix)) continue;

                // Converter cor da API (Hex) para CS2 ChatColor (Ex: {Red})
                string csColor = CS2_TagsHelper.GetClosestChatColor(hexColor ?? "{Default}");
                
                // Aplicar a formatação definida na Config
                string formattedPrefix = Config.PrefixEnabled 
                    ? $"{csColor}{Config.TagPrefix}{prefix}{Config.TagSuffix}{Config.PlayerCustomFont}{Config.PrefixSeparator}" 
                    : ""; 

                // Construir objeto do cargo
                JObject roleTag = new JObject
                {
                    ["prefix"] = formattedPrefix,
                    ["nick_color"] = csColor,
                    ["message_color"] = "{Default}",
                    ["scoreboard"] = $"{prefix} |" 
                };

                tagsRoot[flag] = roleTag;
            }

            // Fallback para jogador padrão se não existir na BD
            if (tagsRoot["everyone"] == null)
            {
                tagsRoot["everyone"] = new JObject
                {
                    ["team_chat"] = false,
                    ["prefix"] = Config.PrefixEnabled ? $"{{Grey}}{Config.TagPrefix}JOGADOR{Config.TagSuffix}{{Default}}{Config.PrefixSeparator}" : "",
                    ["nick_color"] = "",
                    ["message_color"] = "",
                    ["scoreboard"] = "JOGADOR |"
                };
            }

            // Guardar a ordem das flags tal como a API os retornou (ordenados por immunity desc)
            JArray orderedFlagsList = new JArray();
            foreach (JObject role in rolesArray)
            {
                string? flag = role["flag"]?.ToString();
                if (!string.IsNullOrEmpty(flag)) { orderedFlagsList.Add(flag); }
            }
            orderedFlagsList.Add("everyone");

            OrderedFlags = orderedFlagsList;
            newTagsStructure["tags"] = tagsRoot;
            newTagsStructure["ordered_flags"] = OrderedFlags;
            
            JsonTags = newTagsStructure;

            // Fazer backup para ficheiro local
            SaveJsonBackup(ModuleDirectory + "/tags.json");

            // Server.PrintToConsole($"[CS2-Tags] [API] Successfully updated {tagsRoot.Count} tags.");
        }
        catch (Exception ex)
        {
            Server.NextFrame(() => Server.PrintToConsole($"[CS2-Tags] [API] Error fetching tags: {ex.Message}. Using backup."));
        }
    }

    private async Task FetchPlayersTags(List<string> steamids)
    {
        if (steamids == null || steamids.Count == 0) return;

        try
        {
            string ids = string.Join(",", steamids);
            string url = $"{Config.ApiUrl.TrimEnd('/')}/perms/player?steamids={ids}";
            string timeStart = DateTime.Now.ToString("HH:mm:ss");
            if (Config.Debug) Server.PrintToConsole($"[CS2-Tags] [{timeStart}] Fetching tags for {steamids.Count} players...");
            HttpResponseMessage response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            string jsonResponse = await response.Content.ReadAsStringAsync();
            JObject apiData = JObject.Parse(jsonResponse);

            if (apiData["success"]?.Value<bool>() == true && apiData["data"] != null)
            {
                var results = new List<(string sid, string flag)>();
                var dataToken = apiData["data"];

                // Função auxiliar para processar um único jogador
                Action<JObject> processPlayer = (playerObj) => {
                    string? sid = playerObj["steamid"]?.ToString();
                    JToken? roleToken = playerObj["role"];
                    
                    // SEGURANÇA: Verificar se "role" é um objeto antes de indexar ["flag"]
                    // O erro "Cannot access child value on JValue" acontecia aqui se role fosse null/string
                    string? flag = (roleToken is JObject roleObj) ? roleObj["flag"]?.ToString() : null;

                    if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(flag))
                    {
                        results.Add((sid, flag));
                    }
                };

                // Tratar tanto Array como Objeto único por segurança
                if (dataToken is JArray datArr)
                {
                    foreach (var token in datArr)
                    {
                        if (token is JObject obj) processPlayer(obj);
                    }
                }
                else if (dataToken is JObject singleObj)
                {
                    processPlayer(singleObj);
                }

                // Voltar para a Main Thread para atualizar o jogo
                Server.NextFrame(() => {
                    foreach (var res in results)
                    {
                        PlayerAssignedFlags[res.sid] = res.flag;
                        var p = Utilities.GetPlayers().FirstOrDefault(pl => pl.AuthorizedSteamID?.SteamId64.ToString() == res.sid);
                        if (p != null && p.IsValid) SetPlayerClanTag(p);
                    }

                    // Logar na consola as tags de quem está no servidor com timestamp
                    if (Config.Debug)
                    {
                        string nowSafe = DateTime.Now.ToString("HH:mm:ss");
                        Server.PrintToConsole($"[CS2-Tags] [{nowSafe}] --- Jogadores Online e Tags ---");
                        foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot))
                        {
                            string sid = p.AuthorizedSteamID?.SteamId64.ToString() ?? "";
                            if (PlayerAssignedFlags.TryGetValue(sid, out var flag))
                                Server.PrintToConsole($"[CS2-Tags] [{nowSafe}] Player: {p.PlayerName} | Flag: {flag}");
                            else 
                                Server.PrintToConsole($"[CS2-Tags] [{nowSafe}] Player: {p.PlayerName} | Flag: (not assigned)");
                        }
                        Server.PrintToConsole($"[CS2-Tags] [{nowSafe}] --------------------------------");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Server.NextFrame(() => Server.PrintToConsole($"[CS2-Tags] [API] Error fetching players tags: {ex.Message}"));
        }
    }

    private void SaveJsonBackup(string filepath)
    {
        if (JsonTags == null) return;
        try
        {
            File.WriteAllText(filepath, JsonTags.ToString());
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[CS2-Tags] [IO] Error saving backup: {ex.Message}");
        }
    }

    private static void LoadJsonBackup(string filepath)
    {
        try
        {
            if (File.Exists(filepath))
            {
                var jsonData = File.ReadAllText(filepath);
                JsonTags = JObject.Parse(jsonData);
                if (JsonTags["ordered_flags"] is JArray arr)
                {
                    OrderedFlags = arr;
                }
            }
        }
        catch(Exception ex)
        {
             Server.PrintToConsole($"[CS2-Tags] [IO] Error loading backup: {ex.Message}");
        }
    }

    private void OnMapStart(string mapName)
    {
        GaggedIds.Clear();
    }

    [ConsoleCommand("css_tags_reload")]
    public void OnReloadConfig(CCSPlayerController? player, CommandInfo info)
    {
        if (player != null && !AdminManager.PlayerHasPermissions(player, "@css/root")) return;
        
        LoadJsonBackup(ModuleDirectory + "/tags.json"); // Carrega o Backup local
        _ = FetchTagsFromApi(); // Força fetch da API no reload
        
        Server.PrintToConsole("[CS2-Tags] Config reloaded and API fetched!");
        if (player != null) player.PrintToChat("[CS2-Tags] Config reloaded and API fetched!");
    }

    [ConsoleCommand("css_tag_mute")]
    [CommandHelper(minArgs: 1, usage: "<SteamID>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnTagMuteCommand(CCSPlayerController? caller, CommandInfo command)
    {
        string? steamid = command.GetArg(1);
        if (steamid.Length == 17 && !GaggedIds.Contains(steamid))
        {
            GaggedIds.Add(steamid);
        }
    }

    [ConsoleCommand("css_tag_unmute")]
    [CommandHelper(minArgs: 1, usage: "<SteamID>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnTagUnMuteCommand(CCSPlayerController? caller, CommandInfo command)
    {
        string? steamid = command.GetArg(1);
        if (steamid.Length == 17 && GaggedIds.Contains(steamid))
        {
            GaggedIds.Remove(steamid);
        }
    }

    private void OnClientAuthorized(int playerSlot, SteamID steamId)
    {
        CCSPlayerController? player = Utilities.GetPlayerFromSlot(playerSlot);
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;

        _ = FetchPlayersTags(new List<string> { steamId.SteamId64.ToString() });
        AddTimer(2.5f, () => SetPlayerClanTag(player));
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return HookResult.Continue;

        _ = FetchPlayersTags(new List<string> { player.AuthorizedSteamID!.SteamId64.ToString() });
        AddTimer(2.5f, () => SetPlayerClanTag(player));
        return HookResult.Continue;
    }

    private void OnClientDisconnect(int playerSlot)
    {
        CCSPlayerController? player = Utilities.GetPlayerFromSlot(playerSlot);
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;

        string sid = player.AuthorizedSteamID?.SteamId64.ToString() ?? "";
        if (!string.IsNullOrEmpty(sid)) PlayerAssignedFlags.TryRemove(sid, out _);
        GaggedIds.Remove(player.SteamID.ToString()!);
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        AddTimer(1.5f, () => SetPlayerClanTag(player));
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        AddTimer(1.5f, () => SetPlayerClanTag(player));
        return HookResult.Continue;
    }

    private HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || info.GetArg(1).Length == 0 || player.AuthorizedSteamID == null) return HookResult.Continue;
        string steamid = player.AuthorizedSteamID.SteamId64.ToString();

        if (player.SteamID.ToString() != "" && GaggedIds.Contains(player.SteamID.ToString())) return HookResult.Handled;

        if (info.GetArg(1).StartsWith("!") || info.GetArg(1).StartsWith("@") || info.GetArg(1).StartsWith("/") || info.GetArg(1).StartsWith(".") || info.GetArg(1) == "rtv") return HookResult.Continue;

        if (JsonTags != null && JsonTags.TryGetValue("tags", out var tags) && tags is JObject tagsObject)
        {
            string deadIcon = !player.PawnIsAlive ? $"{ChatColors.White}☠ {ChatColors.Default}" : "";

            // Prioridade 1: Flag específica atribuída pela API (Precisão de 100%)
            if (PlayerAssignedFlags.TryGetValue(steamid, out var assignedFlag) && tagsObject.TryGetValue(assignedFlag, out var assignedTag) && assignedTag is JObject)
            {
                string prefix = assignedTag["prefix"]?.ToString() ?? "";
                string nickColor = assignedTag?["nick_color"]?.ToString() ?? ChatColors.Default.ToString();
                string messageColor = assignedTag?["message_color"]?.ToString() ?? ChatColors.Default.ToString();

                Server.PrintToChatAll(ReplaceTags($" {deadIcon}{prefix}{nickColor}{player.PlayerName}{ChatColors.Default}: {messageColor}{info.GetArg(1)}", player.TeamNum));
                return HookResult.Handled;
            }

            // Prioridade 2: SteamID Específico no JSON
            if (tagsObject.TryGetValue(steamid, out var playerTag) && playerTag is JObject)
            {
                string prefix = playerTag["prefix"]?.ToString() ?? "";
                string nickColor = playerTag?["nick_color"]?.ToString() ?? ChatColors.Default.ToString();
                string messageColor = playerTag?["message_color"]?.ToString() ?? ChatColors.Default.ToString();

                Server.PrintToChatAll(ReplaceTags($" {deadIcon}{prefix}{nickColor}{player.PlayerName}{ChatColors.Default}: {messageColor}{info.GetArg(1)}", player.TeamNum));
                return HookResult.Handled;
            }

            // Prioridade 2: Flag/Permissões na ordem correta de Immunity
            if (OrderedFlags != null)
            {
                foreach (var token in OrderedFlags)
                {
                    string groupOrPerm = token.ToString();
                    if (groupOrPerm == "everyone") continue; // Processado no Priority 3
                    
                    bool hasPerm = groupOrPerm.StartsWith("#") ? AdminManager.PlayerInGroup(player, groupOrPerm) : AdminManager.PlayerHasPermissions(player, groupOrPerm);

                    if (hasPerm && tagsObject.TryGetValue(groupOrPerm, out var permTag) && permTag is JObject)
                    {
                        string prefix = permTag["prefix"]?.ToString() ?? "";
                        string nickColor = permTag["nick_color"]?.ToString() ?? ChatColors.Default.ToString();
                        string messageColor = permTag["message_color"]?.ToString() ?? ChatColors.Default.ToString();

                        Server.PrintToChatAll(ReplaceTags($" {deadIcon}{prefix}{nickColor}{player.PlayerName}{ChatColors.Default}: {messageColor}{info.GetArg(1)}", player.TeamNum));
                        return HookResult.Handled;
                    }
                }
            }

            // Prioridade 3: Everyone fallback
            if (tagsObject.TryGetValue("everyone", out var everyoneTag) && everyoneTag is JObject everyoneObject && everyoneObject["team_chat"]?.Value<bool>() != true)
            {
                string prefix = everyoneObject["prefix"]?.ToString() ?? "";
                string nickColor = everyoneObject["nick_color"]?.ToString() ?? ChatColors.Default.ToString();
                string messageColor = everyoneObject["message_color"]?.ToString() ?? ChatColors.Default.ToString();

                Server.PrintToChatAll(ReplaceTags($" {deadIcon}{prefix}{nickColor}{player.PlayerName}{ChatColors.Default}: {messageColor}{info.GetArg(1)}", player.TeamNum));
                return HookResult.Handled;
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerChatTeam(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || info.GetArg(1).Length == 0 || player.AuthorizedSteamID == null) return HookResult.Continue;
        string steamid = player.AuthorizedSteamID.SteamId64.ToString();

        if (player.SteamID.ToString() != "" && GaggedIds.Contains(player.SteamID.ToString())) return HookResult.Handled;

        if (info.GetArg(1).StartsWith("@") && AdminManager.PlayerHasPermissions(player, "@css/chat"))
        {
            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot && !p.IsHLTV && AdminManager.PlayerHasPermissions(p, "@css/chat")))
            {
                p.PrintToChat($" {ChatColors.Lime}(ADMIN) {ChatColors.Default}{player.PlayerName}: {info.GetArg(1).Remove(0, 1)}");
            }
            return HookResult.Handled;
        }

        if (info.GetArg(1).StartsWith("!") || info.GetArg(1).StartsWith("@") || info.GetArg(1).StartsWith("/") || info.GetArg(1).StartsWith(".") || info.GetArg(1) == "rtv") return HookResult.Continue;

        if (JsonTags != null && JsonTags.TryGetValue("tags", out var tags) && tags is JObject tagsObject)
        {
            string deadIcon = !player.PawnIsAlive ? $"{ChatColors.White}☠ {ChatColors.Default}" : "";
            
            // Prioridade 1: Flag específica atribuída pela API
            if (PlayerAssignedFlags.TryGetValue(steamid, out var assignedFlag) && tagsObject.TryGetValue(assignedFlag, out var assignedTag) && assignedTag is JObject)
            {
                string prefix = assignedTag["prefix"]?.ToString() ?? "";
                string nickColor = assignedTag?["nick_color"]?.ToString() ?? ChatColors.Default.ToString();
                string messageColor = assignedTag?["message_color"]?.ToString() ?? ChatColors.Default.ToString();

                foreach (var p in Utilities.GetPlayers().Where(p => p.TeamNum == player.TeamNum && p.IsValid && !p.IsBot))
                {
                    string messageToSend = $"{deadIcon}{TeamName(player.TeamNum)} {ChatColors.Default}{prefix}{nickColor}{player.PlayerName}{ChatColors.Default}: {messageColor}{info.GetArg(1)}";
                    p.PrintToChat($" {ReplaceTags(messageToSend, p.TeamNum)}");
                }
                return HookResult.Handled;
            }

            // Prioridade 2: SteamID
            if (tagsObject.TryGetValue(steamid, out var playerTag) && playerTag is JObject)
            {
                string prefix = playerTag["prefix"]?.ToString() ?? "";
                string nickColor = playerTag?["nick_color"]?.ToString() ?? ChatColors.Default.ToString();
                string messageColor = playerTag?["message_color"]?.ToString() ?? ChatColors.Default.ToString();

                foreach (var p in Utilities.GetPlayers().Where(p => p.TeamNum == player.TeamNum && p.IsValid && !p.IsBot))
                {
                    string messageToSend = $"{deadIcon}{TeamName(player.TeamNum)} {ChatColors.Default}{prefix}{nickColor}{player.PlayerName}{ChatColors.Default}: {messageColor}{info.GetArg(1)}";
                    p.PrintToChat($" {ReplaceTags(messageToSend, p.TeamNum)}");
                }
                return HookResult.Handled;
            }

            // Prioridade 2: Flag/Perms na ordem correta de Immunity
            if (OrderedFlags != null)
            {
                foreach (var token in OrderedFlags)
                {
                    string groupOrPerm = token.ToString();
                    if (groupOrPerm == "everyone") continue;
                    
                    bool hasPerm = groupOrPerm.StartsWith("#") ? AdminManager.PlayerInGroup(player, groupOrPerm) : AdminManager.PlayerHasPermissions(player, groupOrPerm);

                    if (hasPerm && tagsObject.TryGetValue(groupOrPerm, out var permTag) && permTag is JObject)
                    {
                        string prefix = permTag["prefix"]?.ToString() ?? "";
                        string nickColor = permTag["nick_color"]?.ToString() ?? ChatColors.Default.ToString();
                        string messageColor = permTag["message_color"]?.ToString() ?? ChatColors.Default.ToString();

                        foreach (var p in Utilities.GetPlayers().Where(p => p.TeamNum == player.TeamNum && p.IsValid && !p.IsBot))
                        {
                            string messageToSend = $"{deadIcon}{TeamName(player.TeamNum)} {ChatColors.Default}{prefix}{nickColor}{player.PlayerName}{ChatColors.Default}: {messageColor}{info.GetArg(1)}";
                            p.PrintToChat($" {ReplaceTags(messageToSend, p.TeamNum)}");
                        }
                        return HookResult.Handled;
                    }
                }
            }

            // Prioridade 3: Everyone fallback
            if (tagsObject.TryGetValue("everyone", out var everyoneTag) && everyoneTag is JObject)
            {
                string prefix = everyoneTag["prefix"]?.ToString() ?? "";
                string nickColor = everyoneTag["nick_color"]?.ToString() ?? ChatColors.Default.ToString();
                string messageColor = everyoneTag["message_color"]?.ToString() ?? ChatColors.Default.ToString();

                foreach (var p in Utilities.GetPlayers().Where(p => p.TeamNum == player.TeamNum && p.IsValid && !p.IsBot))
                {
                    string messageToSend = $"{deadIcon}{TeamName(player.TeamNum)} {ChatColors.Default}{prefix}{nickColor}{player.PlayerName}{ChatColors.Default}: {messageColor}{info.GetArg(1)}";
                    p.PrintToChat($" {ReplaceTags(messageToSend, p.TeamNum)}");
                }
                return HookResult.Handled;
            }
        }
        return HookResult.Continue;
    }

    private void SetPlayerClanTag(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV || player.AuthorizedSteamID == null) return;

        string steamid = player.AuthorizedSteamID.SteamId64.ToString();

        if (JsonTags != null && JsonTags.TryGetValue("tags", out var tags) && tags is JObject tagsObject)
        {
            string? foundScoreboard = null;

            // Prioridade 1: Flag atribuída
            if (PlayerAssignedFlags.TryGetValue(steamid, out var assignedFlag) && tagsObject.TryGetValue(assignedFlag, out var assignedTag) && assignedTag is JObject)
            {
                foundScoreboard = assignedTag["scoreboard"]?.ToString();
            }

            // Prioridade 2: SteamID
            if (foundScoreboard == null && tagsObject.TryGetValue(steamid, out var playerTag) && playerTag is JObject)
            {
                foundScoreboard = playerTag["scoreboard"]?.ToString();
            }

            // Prioridade 3: Ordered Flags
            if (foundScoreboard == null && OrderedFlags != null)
            {
                foreach (var token in OrderedFlags)
                {
                    string groupOrPerm = token.ToString();
                    if (groupOrPerm == "everyone") continue;
                    
                    bool hasPerm = groupOrPerm.StartsWith("#") ? AdminManager.PlayerInGroup(player, groupOrPerm) : AdminManager.PlayerHasPermissions(player, groupOrPerm);

                    if (hasPerm && tagsObject.TryGetValue(groupOrPerm, out var permTag) && permTag is JObject)
                    {
                        foundScoreboard = permTag["scoreboard"]?.ToString();
                        if (foundScoreboard != null) break;
                    }
                }
            }

            // Prioridade 4: Everyone
            if (foundScoreboard == null && tagsObject.TryGetValue("everyone", out var everyone) && everyone is JObject everyoneObject)
            {
                foundScoreboard = everyoneObject["scoreboard"]?.ToString();
            }

            if (foundScoreboard != null)
            {
                if (player.Clan != foundScoreboard)
                {
                    // Força a atualização no TAB (Scoreboard) limpando e setando novamente
                    player.Clan = "";
                    Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");

                    var pawn = player.PlayerPawn.Value;

                    AddTimer(0.1f, () => {
                        if (player != null && player.IsValid)
                        {
                            player.Clan = foundScoreboard;
                            Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
                            
                            // Em alguns casos, o Pawn também precisa de refresh se houver flags de clan associadas
                            if (pawn != null && pawn.IsValid)
                            {
                                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iTeamNum"); // Trigger genérico de refresh
                            }
                        }
                    });
                }
            }
        }
    }

    private string TeamName(int teamNum)
    {
        return teamNum switch
        {
            0 => "(NONE)",
            1 => "(SPEC)",
            2 => $"{ChatColors.Yellow}(T)",
            3 => $"{ChatColors.Blue}(CT)",
            _ => ""
        };
    }

    private string TeamColor(int teamNum)
    {
        return teamNum switch
        {
            2 => $"{ChatColors.Gold}",
            3 => $"{ChatColors.Blue}",
            _ => ""
        };
    }

    private string ReplaceTags(string message, int teamNum = 0)
    {
        if (message.Contains('{'))
        {
            string modifiedValue = message;
            foreach (FieldInfo field in typeof(ChatColors).GetFields())
            {
                string pattern = $"{{{field.Name}}}";
                if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    modifiedValue = modifiedValue.Replace(pattern, field.GetValue(null)!.ToString()!, StringComparison.OrdinalIgnoreCase);
                }
            }
            return modifiedValue.Replace("{TEAMCOLOR}", TeamColor(teamNum));
        }
        return message;
    }
}
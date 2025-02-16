using System.Net;
using System.Net.Http.Json;
using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;
using NtpClient;

using FatKATT;
using NSDotnet;
using NSDotnet.Models;

#region License
#endregion

var app = new CommandApp<FattKATTCommand>();
return await app.RunAsync(args);

class FattKATTCommand : AsyncCommand<FattKATTCommand.Settings>
{
    const string VersionNumber = "1.4.0";

    int PollSpeed = 750;
    NtpConnection ntpConnection;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-n|--nation"), Description("The nation using FattKATT")]
        public string? Nation { get; init; }

        [CommandOption("-t|--triggers"), Description("A comma-separated list of triggers to use instead of trigger_list.txt")]
        public string? Triggers { get; init; }

        [CommandOption("-p|--poll-speed"), Description("Poll speed, minimum is 750")]
        public int? PollSpeed { get; init; }

        [CommandOption("-b|--beep"), Description("If you want FattKATT to beep when a target updates")]
        public bool? Beep { get; init; }
    }

    public async override Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings)
    {

        // Set up NSDotNet
        var API = NSAPI.Instance;
        API.UserAgent = $"FatKATT/{VersionNumber} (By 20XX, Atagait@hotmail.com)";
        

        PrintSplash();


        Logger.Request("Checking for newer versions...");
        {
            HttpClient httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"FattKATT/{VersionNumber} (https://github.com/vleerian/fattkatt)");
            var gitReq = await httpClient.GetAsync("https://api.github.com/repos/vleerian/fattkatt/releases/latest");
            var versionInfo = await gitReq.Content.ReadFromJsonAsync<GithubAPI>();
            int result = CompareVersion(versionInfo!.Tag_Name!, VersionNumber);
            switch(result)
            {
                case 0: Logger.Info("A newer version of FattKATT has been released https://github.com/Vleerian/FattKATT/releases/latest"); break;
                case 1: Logger.Warning("You are using a bleeding-edge build of FattKATT, it is reccommended to use the latest official release."); break;
                case 2: Logger.Info("FattKATT is up to date!"); break;
                case 3: Logger.Warning("Invalid semantic versioning - it is recommended to use the latest official release."); break;
                case 4: Logger.Warning("You are using an experimental build, here be dragons."); break;
            }
            httpClient.Dispose();
        }

        #region UserIdentification
        string? UserNation = settings.Nation;
        if(settings.Nation == null)
        {
            AnsiConsole.WriteLine("FatKATT requires your nation to inform NS Admin who is using it.");
            UserNation= AnsiConsole.Ask<string>("Please provide your [green]nation[/]: ");
        }
        var r = await API.MakeRequest($"https://www.nationstates.net/cgi-bin/api.cgi?nation={Helpers.SanitizeName(UserNation)}");
        if(r.StatusCode != HttpStatusCode.OK)
        {
            // An error message specifically for 404 since that is the result of a user error
            if(r.StatusCode == HttpStatusCode.NotFound)
                Logger.Error("The provided nation does not exist.");
            else
                Logger.Error($"NS replied with {(int)r.StatusCode}: {r.StatusCode.ToString()}, shutting down");
            return 1;
        }
        Logger.Info($"NS has seen {NSAPI.Instance.Status!.RequestsSeen} requests from you.");

        NationAPI Nation = Helpers.BetterDeserialize<NationAPI>(await r.Content.ReadAsStringAsync());
        API.UserAgent = $"FatKATT/{VersionNumber} (By 20XX, Atagait@hotmail.com - In Use by {UserNation})";
        Logger.Info($"You have identified as {Nation.fullname}.");
        #endregion

        if(settings.PollSpeed != null)
            PollSpeed = settings.PollSpeed <= 650 ? (int)settings.PollSpeed : 750;
        else
            PollSpeed = AnsiConsole.Prompt(new TextPrompt<int>("How many miliseconds should FattKATT wait between NS API requests? ")
                .DefaultValue(750)
                .ValidationErrorMessage("[red]Invalid poll speed.[/]")
                .Validate(s => s switch {
                    < 650 => ValidationResult.Error("[red]Poll speed too low. Minimum 650[/]"),
                    _ => ValidationResult.Success(),
                    })
                );

        bool Beep;
        if(settings.Beep != null)
            Beep = (bool)settings.Beep;
        else
            Beep = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("Enable Beeping?")
                .AddChoices(new[] { "Yes", "No" })) == "Yes" ;

        #region PreProcessTriggerList
        List<string> Triggers = null;
        while(Triggers == null)
        {
            string[] triggers;
            if(settings.Triggers == null || settings.Triggers.Length == 0)
            {
                Logger.Processing("Loading trigger regions from trigger_list.txt");
                if(!File.Exists("./trigger_list.txt"))
                {
                    File.WriteAllText("./trigger_list.txt", "#trigger_list.txt\n#format is 1 trigger region per line.\n#lines can be commented out with hash marks.\n#parts of lines after hash marks are also commented out.");
                    Logger.Info("File does not exist. Template created, please populate trigger_list.txt with list of trigger regions.");
                    Console.WriteLine("Press ENTER to continue."); Console.ReadLine();
                }
                triggers = File.ReadAllText("./trigger_list.txt").Split("\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                triggers = triggers.Select(L=>L.Split("#").First().Trim().Replace(' ','_')).Where(L => !string.IsNullOrEmpty(L) && !L.StartsWith("#")).ToArray();
                if(triggers.Length == 0)
                {
                    Logger.Error("Trigger list is empty. Please populate trigger_list.txt with list of trigger regions.");
                    Console.WriteLine("Press ENTER to continue."); Console.ReadLine();
                }
                Triggers = triggers.ToList();
            }
            else
            {
                Triggers = settings.Triggers.Split(',')
                    .Select(T=>Helpers.SanitizeName(T))
                    .ToList();
            }
        }
        #endregion


        ntpConnection = new NtpConnection("pool.ntp.org");
        int current_time = CurrentTimestamp();


        Logger.Info("Sorting triggers.");
        Logger.Info($"This will take ~{(Triggers.Count * PollSpeed) / 1000} seconds.");
        List<(double timestamp, string trigger)> Sorted_Triggers = new();
        foreach (string trigger in Triggers)
        {
            await Task.Delay(PollSpeed);
            Logger.Request($"Getting LastUpdate for {trigger}");
            var req = await API.MakeRequest($"https://www.nationstates.net/cgi-bin/api.cgi?region={trigger}&q=lastupdate+name");
            if(req.StatusCode != HttpStatusCode.OK)
            {
                Logger.Error($"Failed to fetch data for {trigger}. It will not be checked for updates.");
                continue;
            }

            RegionAPI Region = Helpers.BetterDeserialize<RegionAPI>(await req.Content.ReadAsStringAsync());
            if(Region.LastUpdate == null)
                Logger.Warning($"{trigger} is a new region.");
            else if (current_time - Region.LastUpdate < 7200)
                Logger.Warning($"{trigger} has already updated.");
            else
                Sorted_Triggers.Add((Region.LastUpdate, trigger));
        }
        // Sort all the triggers by their lastupdate
        Sorted_Triggers.Sort((x, y) => x.timestamp.CompareTo(y.timestamp));
        Logger.Info($"Sorted {Sorted_Triggers.Count} triggers.");

        await AnsiConsole.Progress()
            .AutoClear(false)
            .AutoRefresh(true)
            .HideCompleted(false)
            .Columns(new ProgressColumn[] {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new SpinnerColumn()
            })
            .StartAsync(async ctx => {

            ProgressTask ProgTask = ctx.AddTask("Waiting for next update...", maxValue: Sorted_Triggers.Count);
            while(Sorted_Triggers.Count > 0)
            {
                var Trigger = Sorted_Triggers.First();
                ProgTask.Description = $"Waiting for {Trigger.trigger}";
                RegionAPI Region;
                try {
                    await Task.Delay(PollSpeed);
                    var req = await API.MakeRequest($"https://www.nationstates.net/cgi-bin/api.cgi?region={Trigger.trigger}&q=lastupdate");
                    if(req.StatusCode == HttpStatusCode.NotFound)
                    {
                        Logger.Warning("Target cannot be found, skipping");
                        ProgTask.Increment(1.0);
                        Sorted_Triggers.Remove(Trigger);
                    }
                    Region = Helpers.BetterDeserialize<RegionAPI>(await req.Content.ReadAsStringAsync());
                }
                catch ( HttpRequestException e )
                {
                    // Error handling for rate limit being exceeded, in the case that an exception is thrown
                    if(e.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        Logger.Warning("Rate limit exceeded. Sleeping for 5 seconds.");
                        Thread.Sleep(5000);
                    }
                    else
                        Logger.Warning("Error loading region data ");
                    break;
                }

                if(Trigger.timestamp != Region.LastUpdate)
                {
                    AnsiConsole.MarkupLine($"[red]!!![/] - [yellow]UPDATE DETECTED IN {Trigger.trigger}[/] - [red]!!![/]");
                    if(Beep)
                        Console.Beep();
                    ProgTask.Increment(1.0);
                    Sorted_Triggers.Remove(Trigger);
                }
            }
        });

        Logger.Info("All targets have updated, shutting down.");
        Console.WriteLine("Press ENTER to continue."); Console.ReadLine();

        return 0;
    }

    int CompareVersion(string VersionA, string VersionB)
    {
        // (StringSplitOptions)3 is shorthand for trim entries and remove empty entries
        var PartsA = VersionA.Split('.', 3, (StringSplitOptions)3);
        if(PartsA.Length < 3)
            return 3;
        var PartsB = VersionB.Split('.', 3, (StringSplitOptions)3);
        if(PartsB.Length < 3)
            return 3;
        return CompareVersionParts(PartsA, PartsB, 0);
    }

    int CompareVersionParts(string[] PartsA, string[] PartsB, int index)
    {
        int ResultA, ResultB;
        if(index >= PartsA.Length)
            return 2;
        else if(!Int32.TryParse(PartsA[index], out ResultA))
            return 4;
        else if(!Int32.TryParse(PartsB[index], out ResultB))
            return 4;
        if(ResultA > ResultB)
            return 0;
        else if(ResultA < ResultB)
            return 1;
        return CompareVersionParts(PartsA, PartsB, ++index);
    }

    int CheckRatelimit(HttpResponseMessage r)
    {
        string strRatelimitSeen = r.Headers.GetValues("X-ratelimit-requests-seen").First();
        return Int32.Parse(strRatelimitSeen);
    }

    int CurrentTimestamp()
    {
        var utcNow = ntpConnection.GetUtc(); 
        TimeSpan t = utcNow - new DateTime(1970, 1, 1);
        return (int)t.TotalSeconds;
    }

    public void PrintSplash()
    {
        AnsiConsole.WriteLine("Khron and Atagait's Triggering Tool");

        AnsiConsole.WriteLine($"FatKATT Version {VersionNumber}.");
        AnsiConsole.WriteLine($"This software is provided as-is, without warranty of any kind.");
    }
}

[Serializable]
public class GithubAPI
{
    [JsonPropertyName("tag_name")]
    public string? Tag_Name { get; init; }

    [JsonPropertyName("name")]
    public string? Release_Name { get; init; }

    [JsonPropertyName("published_at")]
    public string? published { get; init; }

    [JsonIgnore]
    public DateTime Published => DateTime.Parse(published);
}

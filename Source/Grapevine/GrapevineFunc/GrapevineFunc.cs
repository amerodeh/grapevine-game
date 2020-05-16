using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Grapevine.GameRunner.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;

namespace GrapevineFunc
{
    public static class GrapevineFunc
    {
        [FunctionName("GrapevineFunc")]
        public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "")] HttpRequest req,
        ILogger logger)
        {
            _logger = logger;
            var headerValue = req.Headers["GrapevineAction"];
            _logger.LogDebug($"Parsed header '{headerValue}'");

            switch (headerValue)
            {
                case "whisper":
                    var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                    logger.LogDebug($"Received whisper: '{requestBody}'");
                    var data = JsonConvert.DeserializeObject<MessageRequest>(requestBody);
                    return await HandleWhisper(data);
                case "games":
                    logger.LogDebug("All games requested.");
                    return await GetAllGames();
                case "game":
                    var gameId = req.Headers["GameId"];
                    logger.LogDebug($"Requested game '{gameId}'");
                    return await GetGame(gameId);
                default:
                    logger.LogDebug($"Bad request: {await new StreamReader(req.Body).ReadToEndAsync()}");
                    return new BadRequestResult();
            }
        }

        private static async Task<IActionResult> HandleWhisper(MessageRequest whisper)
        {
            _logger.LogDebug($"Handling whisper '{JsonConvert.SerializeObject(whisper)}'");

            LogWhisper(whisper);

            if (whisper.SentFromId == -1) // sent from game initiator
            {
                _logger.LogDebug("sent from game init");
                BlobUtilities.WriteGameStartTime(whisper.GameId);
                BlobUtilities.WriteStartMessage(whisper.GameId, whisper.Message);
                InterpretWhisper(whisper);
                UpdateNextRecipientId(whisper);
                await SendWhisperToUrl(GetNextRecipientUrl(whisper), whisper);
            }
            else if (whisper.SentFromId == whisper.WhisperRecipients.Last().Id) // received from last player, game over, write results
            {
                _logger.LogDebug($"{whisper.GameId} sent from last player");
                BlobUtilities.WriteGame(
                    whisper.GameId,
                    await BlobUtilities.GetGameStartTime(whisper.GameId),
                    GetCurrentTime,
                    await BlobUtilities.GetStartMessage(whisper.GameId),
                    whisper.Message);
            }
            else
            {
                _logger.LogDebug($"{whisper.GameId} sent from normal player");
                InterpretWhisper(whisper);
                UpdateNextRecipientId(whisper);
                await SendWhisperToUrl(GetNextRecipientUrl(whisper), whisper);
            }

            return new OkResult();
        }

        private static void InterpretWhisper(in MessageRequest whisper)
        {
            _logger.LogDebug($"{whisper.GameId} interpreting");
            var wordToReplace = SelectWord(whisper.Message);
            var replacementWord = GetRhymingWord(wordToReplace).Result;
            whisper.Message = ReplaceWord(whisper.Message, wordToReplace, replacementWord);
        }

        private static string ReplaceWord(string message, string wordToReplace, string replacementWord)
        {
            int indexToReplace;
            if (new Random().Next() % 2 == 0)
            {
                indexToReplace = message.IndexOf(wordToReplace, StringComparison.InvariantCultureIgnoreCase);
            }
            else
            {
                indexToReplace = message.LastIndexOf(wordToReplace, StringComparison.InvariantCultureIgnoreCase);
            }

            return message.Remove(indexToReplace, wordToReplace.Length).Insert(indexToReplace, replacementWord);
        }

        private static async Task SendWhisperToUrl(string url, MessageRequest whisper)
        {
            using var httpClient = new HttpClient();
            await httpClient.PostAsJsonAsync(url, whisper);
        }

        private static string SelectWord(string sentence)
        {
            var punctuation = sentence.Where(char.IsPunctuation).Distinct().ToArray();
            var wordsWithoutPunctuation = sentence.Split().Select(x => x.Trim(punctuation)).ToArray();
            var wordsWithoutConjunctions = wordsWithoutPunctuation.Where(word => _conjuctions.Split(',').Contains(word)).ToArray();
            if (wordsWithoutConjunctions.Any())
            {
                wordsWithoutPunctuation = wordsWithoutConjunctions;
            }

            return wordsWithoutPunctuation.ElementAt(new Random().Next(wordsWithoutConjunctions.Length));
        }

        private static async Task<string> GetRhymingWord(string word)
        {
            using var httpClient = new HttpClient();
            var responseString = await httpClient.GetStringAsync($"https://api.datamuse.com/words?rel_rhy={word}");
            dynamic rhymingWords = JsonConvert.DeserializeObject(responseString);
            try
            {
                return rhymingWords[0]["word"];
            }
            catch
            {
                _logger.LogInformation(
                    $"Datamuse can't find a word that rhymes with {word}. Using original word to keep the game going!");
                return word;
            }
        }

        private static async Task<IActionResult> GetGame(StringValues gameId)
        {
            var game = await BlobUtilities.ReadText(await BlobUtilities.GameBlobContainer, $"{gameId}");

            if (string.IsNullOrEmpty(game))
            {
                return new NotFoundResult();
            }

            return new OkObjectResult(game);
        }

        private static async Task<IActionResult> GetAllGames()
        {
            var result = new JsonTextWriter(new StringWriter());

            await result.WriteStartObjectAsync();
            await result.WritePropertyNameAsync("games");
            await result.WriteStartArrayAsync();
            foreach (var game in BlobUtilities.ReadAllBlobsInContainer(await BlobUtilities.GameBlobContainer))
            {
                await result.WriteValueAsync(game);
            }
            await result.WriteEndArrayAsync();
            await result.WriteEndObjectAsync();

            return new OkObjectResult(result.ToString());
        }

        private static string GetNextRecipientUrl(MessageRequest whisper)
        {
            var nextRecipientId = whisper.NextWhisperRecipientId;
            return whisper.WhisperRecipients.Single(r => r.Id == nextRecipientId).Url;
        }

        private static void UpdateNextRecipientId(in MessageRequest whisper)
        {
            var currentNextRecipientId = whisper.NextWhisperRecipientId;
            var newNextRecipientId = 0;
            if (whisper.WhisperRecipients.Count != currentNextRecipientId - 1)
            {
                newNextRecipientId = currentNextRecipientId + 1;

            }

            whisper.NextWhisperRecipientId = newNextRecipientId;
        }

        private static void LogWhisper(MessageRequest whisper)
        {
            _logger.LogInformation($"{whisper.GameId}:{whisper.Message}");
            BlobUtilities.WriteMessage(whisper);
        }

        private static string GetCurrentTime => DateTime.UtcNow.ToString("o");

        private static readonly string _conjuctions =
            "After,Although,As,Because,Before,Even,If,Inasmuch,Lest,Now,Once,Provided,Since,Supposing,Than,That," +
            "Though,Till,Unless,Until,When,Whenever,Where,Whereas,Wherever,Whether,Which,While,Who,Whoever,Why," +
            "Accordingly,Actually,After,Afterward,Also,And,Another,Because,Before,Besides,Briefly,But,Consequently," +
            "Conversely,Equally,Finally,First,Fourth,Further,Furthermore,Gradually,Hence,However,Least,Last,Lastly," +
            "Later,Meanwhile,Moreover,Nevertheless,Next,Nonetheless,Now,Nor,Or,Presently,Second,Similarly,Since,So," +
            "Soon,Still,Subsequently,Then,Thereafter,Therefore,Third,Thus,Too,Ultimately,What,Whatever,Whoever," +
            "Whereas,Whomever,When,While,Yet";

        private static ILogger _logger;
    }
}

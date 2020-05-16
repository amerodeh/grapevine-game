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
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
            _logger.LogInformation($"Parsed header {headerValue}");

            switch (headerValue)
            {
                case "whisper":
                    var requestBody = new StreamReader(req.Body).ReadToEnd();
                    var data = JsonConvert.DeserializeObject<MessageRequest>(requestBody);
                    return await HandleWhisper(data, logger);
                case "allGames":
                    logger.LogInformation("All games requested.");
                    return GetAllGames();
                case "game":
                    var gameId = req.Query["GameId"];
                    logger.LogInformation($"Requested game {gameId}");
                    return GetGame(gameId);
                default:
                    logger.LogInformation("Bad request");
                    return new BadRequestResult();
            }
        }

        private static async Task<IActionResult> HandleWhisper(MessageRequest whisper, ILogger logger)
        {
            LogWhisper(whisper);

            WhisperRecipient nextRecipient;
            if (whisper.SentFromId == -1) // sent from game initiator
            {
                SaveGameStartTimeToBlob(whisper.GameId);
                SaveStartMessageToBlob(whisper.GameId, whisper.Message);
                nextRecipient = whisper.WhisperRecipients.First();
                InterpretWhisper(whisper);
                SendWhisperToRecipient(nextRecipient, whisper);

            }
            else if (whisper.SentFromId == whisper.WhisperRecipients.Last().Id) // received from last player, game over, write results
            {
                WriteGameToBlob(
                    whisper.GameId,
                    GetGameStartTimeFromBlob(whisper.GameId),
                    GetCurrentTime,
                    GetStartMessageFromBlob(whisper.GameId),
                    whisper.Message);
            }
            else if (whisper.SentFromId == whisper.WhisperRecipients[^1].Id) // we're last, interpret message and pass back to initiator
            {
                InterpretWhisper(whisper);
                var initiator = whisper.WhisperRecipients.First();
                SendWhisperToRecipient(initiator, whisper);
            }
            else
            {
                // get my index
                var senderIndex = whisper.WhisperRecipients.Select(r => r.Id).ToList().IndexOf(whisper.SentFromId);
                nextRecipient = whisper.WhisperRecipients[senderIndex + 2]; // +2 since i'm at index +1
            }

            return new OkResult();
        }

        private static void WriteTextToBlob(CloudBlobContainer container, string blobName, string text)
        {
            container.GetBlockBlobReference(blobName).UploadText(text);
        }

        private static string ReadTextFromBlob(CloudBlobContainer container, string blobName)
        {
            return container.GetBlockBlobReference(blobName).DownloadText();
        }

        private static void LogWhisper(MessageRequest whisper)
        {
            _logger.LogTrace($"{whisper.GameId}:{whisper.Message}");

            WriteTextToBlob(
                ReceivedMessagesBlobContainer,
                $"{whisper.GameId}-{GetCurrentTime}",
                whisper.Message);
        }

        private static void SaveGameStartTimeToBlob(int gameId)
        {
            WriteTextToBlob(
                GameStartTimesBlobContainer,
                $"{gameId}",
                GetCurrentTime);
        }

        private static void SaveStartMessageToBlob(int gameId, string startMessage)
        {
            WriteTextToBlob(
                StartMessagesBlobContainer,
                $"{gameId}",
                startMessage);
        }

        private static string GetGameStartTimeFromBlob(int gameId)
        {
            return ReadTextFromBlob(
                GameStartTimesBlobContainer,
                $"{gameId}");
        }

        private static string GetStartMessageFromBlob(int gameId)
        {
            return ReadTextFromBlob(
                StartMessagesBlobContainer,
                $"{gameId}");
        }

        private static void WriteGameToBlob(int gameId, string gameStartTime, string gameEndTime, string startMessage, string endMessage)
        {
            var finishedWhisper = new JObject
            {
                ["gameId"] = gameId,
                ["gameStarted"] = gameStartTime,
                ["gameEnded"] = gameEndTime,
                ["startingMessage"] = startMessage,
                ["endMessage"] = endMessage
            };

            WriteTextToBlob(
                GameBlobContainer,
                $"{gameId}",
                JsonConvert.SerializeObject(finishedWhisper));
        }

        private static void SendWhisperToRecipient(WhisperRecipient nextRecipient, MessageRequest whisper)
        {
            using var httpClient = new HttpClient();
            httpClient.PostAsJsonAsync(nextRecipient.Url, whisper);
        }

        private static void InterpretWhisper(in MessageRequest whisper)
        {
            var wordToReplace = SelectWord(whisper.Message);
            var replacementWord = GetRhymingWord(wordToReplace);
            int indexToReplace;
            if (new Random().Next() % 2 == 0)
            {
                indexToReplace = whisper.Message.IndexOf(wordToReplace, StringComparison.InvariantCultureIgnoreCase);
            }
            else
            {
                indexToReplace = whisper.Message.LastIndexOf(wordToReplace, StringComparison.InvariantCultureIgnoreCase);
            }

            whisper.Message = whisper.Message.Insert(indexToReplace, replacementWord.Result);
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
            catch (Exception)
            {
                _logger.LogInformation(
                    $"Datamuse can't find a word that rhymes with {word}. Using original word to keep the game going!");
                return word;
            }
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

        private static IActionResult GetGame(StringValues gameId)
        {
            var game = GameBlobContainer.GetBlockBlobReference($"{gameId}").DownloadText();

            return new OkObjectResult(game);
        }

        private static IActionResult GetAllGames()
        {
            var result = new JsonTextWriter(new StringWriter());

            result.WriteStartArray();
            foreach (var item in GameBlobContainer.ListBlobs())
            {
                var blob = (CloudBlockBlob)item;
                result.WriteValue(blob.DownloadText());
            }
            result.WriteEndArray();

            return new OkObjectResult(result.ToString());
        }

        private static CloudBlobContainer GetBlobContainer(string containerName)
        {
            var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage", EnvironmentVariableTarget.Process);
            var storageAccount = CloudStorageAccount.Parse(connectionString);
            var serviceClient = storageAccount.CreateCloudBlobClient();
            var container = serviceClient.GetContainerReference($"{containerName}");
            container.CreateIfNotExists();

            return container;
        }

        private static CloudBlobContainer GameBlobContainer => GetBlobContainer("grapevine-games");

        private static CloudBlobContainer GameStartTimesBlobContainer => GetBlobContainer("grapevine-games-start-times");

        private static CloudBlobContainer ReceivedMessagesBlobContainer => GetBlobContainer("grapevine-games-received-messages");

        private static CloudBlobContainer StartMessagesBlobContainer => GetBlobContainer("grapevine-games-start-messages");

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

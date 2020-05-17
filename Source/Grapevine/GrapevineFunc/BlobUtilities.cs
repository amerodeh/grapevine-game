using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grapevine.GameRunner.Models;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrapevineFunc
{
    public static class BlobUtilities
    {
        public static ILogger Logger { private get; set; }

        public static Task<CloudBlobContainer> GameBlobContainer => GetBlobContainer("grapevine-games");

        public static Task<CloudBlobContainer> GameStartTimesBlobContainer => GetBlobContainer("grapevine-games-start-times");

        public static Task<CloudBlobContainer> ReceivedMessagesBlobContainer => GetBlobContainer("grapevine-games-received-messages");

        public static Task<CloudBlobContainer> StartMessagesBlobContainer => GetBlobContainer("grapevine-games-start-messages");

        public static async void WriteGame(int gameId, string gameStartTime, string gameEndTime, string startMessage, string endMessage)
        {
            var finishedWhisper = new JObject
            {
                ["gameId"] = gameId,
                ["gameStarted"] = gameStartTime,
                ["gameEnded"] = gameEndTime,
                ["startingMessage"] = startMessage,
                ["endMessage"] = endMessage
            };

            WriteText(await GameBlobContainer, $"{gameId}", JsonConvert.SerializeObject(finishedWhisper));
        }

        public static List<Task<string>> ReadAllBlobsInContainer(CloudBlobContainer container)
        {
            try
            {
                return container.ListBlobs().Select(async item => await ((CloudBlockBlob)item).DownloadTextAsync()).ToList();
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Failed to read all blobs from container '{container.Name}'");
                return new List<Task<string>>();
            }
        }

        public static async void WriteGameStartTime(int gameId)
        {
            WriteText(await GameStartTimesBlobContainer, $"{gameId}", DateTime.UtcNow.ToString("o"));
        }

        public static async void WriteStartMessage(int gameId, string startMessage)
        {
            WriteText(await StartMessagesBlobContainer, $"{gameId}", startMessage);
        }

        public static async Task<string> GetGameStartTime(int gameId)
        {
            return await ReadText(await GameStartTimesBlobContainer, $"{gameId}");
        }

        public static async Task<string> GetStartMessage(int gameId)
        {
            return await ReadText(await StartMessagesBlobContainer, $"{gameId}");
        }

        public static async void WriteMessage(MessageRequest whisper)
        {
            // Saves a message for easy access
            // Theoretically we can be listed multiple times in the recipients list in a single
            // game hence the logic to append new messages since they're tied to the same game
            var blobText = await ReadText(await ReceivedMessagesBlobContainer, $"{whisper.GameId}.txt");
            blobText += whisper.Message + Environment.NewLine;
            WriteText(await ReceivedMessagesBlobContainer, $"{whisper.GameId}.txt", blobText);
        }

        public static async void WriteText(CloudBlobContainer container, string blobName, string text)
        {
            try
            {
                await container.GetBlockBlobReference($"{blobName}").UploadTextAsync(text);
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Failed to write text to blob '{blobName}' in container '{container.Name}'");
            }
        }

        public static async Task<string> ReadText(CloudBlobContainer container, string blobName)
        {
            try
            {
                return !container.GetBlockBlobReference(blobName).Exists()
                    ? string.Empty
                    : await container.GetBlockBlobReference(blobName).DownloadTextAsync();
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Failed to read text from blob '{blobName}' in container '{container.Name}'");
                return string.Empty;
            }
        }

        public static async Task<CloudBlobContainer> GetBlobContainer(string containerName)
        {
            var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage", EnvironmentVariableTarget.Process);
            var storageAccount = CloudStorageAccount.Parse(connectionString);
            var serviceClient = storageAccount.CreateCloudBlobClient();
            var container = serviceClient.GetContainerReference($"{containerName}");
            await container.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, new BlobRequestOptions(), new OperationContext());

            return container;
        }
    }
}

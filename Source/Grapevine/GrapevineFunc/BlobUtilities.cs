using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grapevine.GameRunner.Models;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrapevineFunc
{
    public static class BlobUtilities
    {
        public static Task<CloudBlobContainer> GameBlobContainer => GetBlobContainer("grapevine-games");

        public static Task<CloudBlobContainer> GameStartTimesBlobContainer => GetBlobContainer("grapevine-games-start-times");

        public static Task<CloudBlobContainer> ReceivedMessagesBlobContainer => GetBlobContainer("grapevine-games-received-messages");

        public static Task<CloudBlobContainer> StartMessagesBlobContainer => GetBlobContainer("grapevine-games-start-messages");

        public static async Task<CloudBlobContainer> GetBlobContainer(string containerName)
        {
            var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage", EnvironmentVariableTarget.Process);
            var storageAccount = CloudStorageAccount.Parse(connectionString);
            var serviceClient = storageAccount.CreateCloudBlobClient();
            var container = serviceClient.GetContainerReference($"{containerName}");
            await container.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, new BlobRequestOptions(), new OperationContext());

            return container;
        }

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

            WriteText(
                await GameBlobContainer,
                $"{gameId}",
                JsonConvert.SerializeObject(finishedWhisper));
        }

        public static async void WriteText(CloudBlobContainer container, string blobName, string text)
        {
            await container.GetBlockBlobReference(blobName).UploadTextAsync(text);
        }

        public static async Task<string> ReadText(CloudBlobContainer container, string blobName)
        {
            return !container.GetBlockBlobReference(blobName).Exists()
                ? string.Empty
                : await container.GetBlockBlobReference(blobName).DownloadTextAsync();
        }

        public static List<Task<string>> ReadAllBlobsInContainer(CloudBlobContainer container)
        {
            return container.ListBlobs().Select(async item => await ((CloudBlockBlob)item).DownloadTextAsync()).ToList();
        }

        public static async void WriteMessage(MessageRequest whisper)
        {
            // Saves a message
            // Theoretically we can be specified multiple times in the recipients list
            // in a single game hence the logic to append new messages
            var blobText = await ReadText(await ReceivedMessagesBlobContainer, $"{whisper.GameId}");
            blobText += whisper.Message + Environment.NewLine;
            WriteText(await ReceivedMessagesBlobContainer, $"{whisper.GameId}", blobText);
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
    }
}

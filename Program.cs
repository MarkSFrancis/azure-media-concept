using System.Runtime.InteropServices;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Media;
using Azure.ResourceManager.Media.Models;
using Azure.Storage.Blobs;
using static System.Console;
using static System.Environment;

var OutputFolder = Path.Join(Environment.GetFolderPath(SpecialFolder.UserProfile), "Downloads", "output");
var InputFileName = Path.Join(Environment.GetFolderPath(SpecialFolder.UserProfile), "Downloads", "input.mp4");
var OutputContainerFolder = "optimized";
var OutputContainerStorageUrl = "https://mediaexamplemoonstar.blob.core.windows.net/files/";

WriteLine("Starting app");

var AZURE_MEDIA_SERVICES_ACCOUNT_NAME = "mediamoonstar";
var AZURE_RESOURCE_GROUP = "streaming-example";
var AZURE_SUBSCRIPTION_ID = "02b34e38-f3f1-4d44-94ad-21d9a06d27ca";

var mediaServicesResourceId = MediaServicesAccountResource.CreateResourceIdentifier(AZURE_SUBSCRIPTION_ID, AZURE_RESOURCE_GROUP, AZURE_MEDIA_SERVICES_ACCOUNT_NAME);

var credentials = new DefaultAzureCredential();
var armClient = new ArmClient(credentials);
var mediaServicesAccount = armClient.GetMediaServicesAccountResource(mediaServicesResourceId);

var mediaId = Guid.NewGuid().ToString()[..13];
var jobName = $"job-{mediaId}";
var inputAssetName = $"input-{mediaId}";
var outputAssetName = $"output-{mediaId}";

MediaTransformResource? transform = null;
MediaAssetResource? inputAsset = null;
MediaAssetResource? outputAsset = null;
MediaJobResource? job = null;

try
{
    WriteLine("Creating transform");
    transform = await CreateTransformAsync(mediaServicesAccount, mediaId);
    WriteLine("Creating input asset");
    inputAsset = await CreateInputAssetAsync(mediaServicesAccount, inputAssetName, InputFileName);
    WriteLine("Creating output asset");
    outputAsset = await CreateOutputAssetAsync(mediaServicesAccount, outputAssetName);

    WriteLine("Executing transform...");
    job = await SubmitJobAsync(transform, jobName, inputAsset, outputAsset);
    job = await WaitForJobCompletionAsync(job);
}
finally
{
    try
    {
        if (job?.Data.State == MediaJobState.Finished && outputAsset is { })
        {
            var outputUris = await ExportOutputToStorageAsync(outputAsset, credentials, OutputContainerStorageUrl, OutputContainerFolder, mediaId);

            WriteLine($"Downloading results from {string.Join(", ", outputUris)}...");
            await DownloadOutputAsync(outputUris, OutputFolder);
        }
        else
        {
            WriteLine("Job failed");
        }
    }
    finally
    {
        await DeleteBlobsAsync(inputAsset, outputAsset);
    }
}

static async Task<MediaTransformResource> CreateTransformAsync(MediaServicesAccountResource account, string transformName)
{
    var transform = await account.GetMediaTransforms().CreateOrUpdateAsync(
        WaitUntil.Completed,
        transformName,
        new MediaTransformData
        {
            Outputs =
            {
                new MediaTransformOutput(
                    preset: new StandardEncoderPreset(
                        codecs: new MediaCodecBase[]
                        {
                            new AacAudio
                            {
                                Channels = 2,
                                SamplingRate = 48000,
                                Bitrate = 128000,
                                Profile = AacAudioProfile.AacLc,
                            },
                            new H264Video
                            {
                                // https://learn.microsoft.com/en-us/azure/media-services/latest/encode-autogen-bitrate-ladder
                                Complexity = H264Complexity.Speed,
                                Layers =
                                {
                                    new H264Layer(bitrate: 1600000)
                                    {
                                        Width = "1280",
                                        Height = "720",
                                        Label = "720p",
                                        FrameRate = "30",
                                    }
                                }
                            }
                        },
                        formats: new MediaFormatBase[]
                        {
                            new Mp4Format("Video-{Basename}-{Label}-{Bitrate}{Extension}")
                        }
                    )
                )
                {
                    OnError = MediaTransformOnErrorType.StopProcessingJob,
                    RelativePriority = MediaJobPriority.Normal,
                }

            },
            Description = "Post video transform"
        }
    );

    return transform.Value;
}

static async Task<MediaAssetResource> CreateInputAssetAsync(MediaServicesAccountResource account, string assetName, string fileToUpload)
{
    MediaAssetResource asset;

    try
    {
        asset = await account.GetMediaAssets().GetAsync(assetName);
        WriteLine($"Warning: asset {assetName} already exists. It will be overwritten.");
    }
    catch (RequestFailedException)
    {
        WriteLine("Creating media asset...");
        var mediaAsset = await account.GetMediaAssets().CreateOrUpdateAsync(
            WaitUntil.Completed,
            assetName,
            new MediaAssetData()
        );

        asset = mediaAsset.Value;
    }

    WriteLine("Getting storage connection...");
    var sasUriCollection = asset.GetStorageContainerUrisAsync(
        new MediaAssetStorageContainerSasContent
        {
            Permissions = MediaAssetContainerPermission.ReadWrite,
            ExpireOn = DateTimeOffset.UtcNow.AddHours(1)
        }
    );

    var sasUri = await sasUriCollection.FirstOrDefaultAsync();

    WriteLine("Connecting to storage...");
    var container = new BlobContainerClient(sasUri);
    var blob = container.GetBlobClient(Path.GetFileName(fileToUpload));

    WriteLine("Uploading media file...");
    await blob.UploadAsync(fileToUpload, overwrite: true);
    WriteLine("Upload complete");

    return asset;
}

static async Task<MediaAssetResource> CreateOutputAssetAsync(MediaServicesAccountResource account, string assetName)
{
    var asset = await account.GetMediaAssets().CreateOrUpdateAsync(
        WaitUntil.Completed,
        assetName,
        new MediaAssetData()
    );

    return asset.Value;
}

static async Task<MediaJobResource> SubmitJobAsync(
    MediaTransformResource transform,
    string jobName,
    MediaAssetResource inputAsset,
    MediaAssetResource outputAsset)
{
    var job = await transform.GetMediaJobs().CreateOrUpdateAsync(
        WaitUntil.Completed,
        jobName,
        new MediaJobData
        {
            Input = new MediaJobInputAsset(inputAsset.Data.Name),
            Outputs =
            {
                new MediaJobOutputAsset(outputAsset.Data.Name)
            }
        }
    );

    return job.Value;
}

static async Task<MediaJobResource> WaitForJobCompletionAsync(MediaJobResource job)
{
    var sleepInterval = TimeSpan.FromSeconds(1);

    var progress = 0;

    do
    {
        job = await job.GetAsync();

        var isProcessing = job.Data.Outputs.Any(o => o.State == MediaJobState.Processing);
        var lowestProgress = job.Data.Outputs.Min(o =>
        {
            if (o.State == MediaJobState.Processing)
            {
                return o.Progress.GetValueOrDefault();
            }
            else if (o.State == MediaJobState.Finished || o.State == MediaJobState.Canceled || o.State == MediaJobState.Error)
            {
                return 100;
            }
            else if (o.State == MediaJobState.Queued || o.State == MediaJobState.Scheduled || o.State == MediaJobState.Canceling)
            {
                return 0;
            }
            else
            {
                throw new ArgumentOutOfRangeException($"Unrecognised state of job: {o.State}");
            }
        });

        progress = lowestProgress;

        if (progress != 100)
        {
            await Task.Delay(sleepInterval);
        }
    } while (progress != 100);

    return job;
}

async static Task<IReadOnlyList<string>> ExportOutputToStorageAsync(MediaAssetResource outputAsset, TokenCredential credentials, string outputContainerStorageUrl, string outputContainerFolder, string mediaId)
{
    WriteLine("Connecting to converted output...");
    var assetContainerSas = outputAsset.GetStorageContainerUrisAsync(new MediaAssetStorageContainerSasContent
    {
        Permissions = MediaAssetContainerPermission.Read,
        ExpireOn = DateTime.UtcNow.AddHours(1),
    });

    var containerSasUrl = await assetContainerSas.FirstAsync();
    
    var results = await CopyAcrossBlobs(containerSasUrl, credentials, outputContainerStorageUrl, outputContainerFolder, mediaId);
    return results;
}

async static Task<IReadOnlyList<string>> CopyAcrossBlobs(Uri inputContainerUrl, TokenCredential credentials, string outputContainerStorageUrl, string outputContainerFolder, string mediaId)
{
    WriteLine("Connecting to input");
    var container = new BlobContainerClient(inputContainerUrl, credentials);

    WriteLine("Connecting to output");
    var outputContainer = new BlobContainerClient(new Uri(outputContainerStorageUrl), credentials);

    WriteLine("Getting files to copy");
    var allFilesToCopy = await container.GetBlobsAsync().ToListAsync();
    WriteLine($"Copying {allFilesToCopy.Count} files to output storage...");

    var outputFiles = new List<string>(allFilesToCopy.Count);
    foreach (var file in allFilesToCopy)
    {
        WriteLine($"Copying {file.Name} to output storage...");
        var outputContainerClient = outputContainer.GetBlobClient($"{outputContainerFolder}/{file.Name}");
        var copyResult = await outputContainerClient.StartCopyFromUriAsync(container.Uri);
        await copyResult.WaitForCompletionAsync();
        WriteLine($"Copied successfully to {outputContainerClient.Uri}.");
    }

    return outputFiles;
}

async static Task DownloadOutputAsync(IEnumerable<string> downloadUris, string outputFolderName)
{
    WriteLine($"Downloading results to {outputFolderName}");

    HttpClient client = new HttpClient();
    foreach (var blobUri in downloadUris)
    {
        var blobRelativePath = blobUri.Split("/optimized/").Last();
        // Get if running on Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            blobRelativePath = blobRelativePath.Replace("/", "\\");
        }
        var outputPath = Path.Combine(outputFolderName, blobRelativePath);
        WriteLine($"Downloading {blobUri} to {outputPath}");

        using var fileStream = new FileStream(outputPath, FileMode.Create);
        using var stream = await client.GetStreamAsync(blobUri);
        await stream.CopyToAsync(fileStream);

        WriteLine($"Downloaded {blobUri}");
    }

    WriteLine("Download complete.");
}

async static Task DeleteBlobsAsync(MediaAssetResource? inputAsset, MediaAssetResource? outputAsset)
{
    await Task.WhenAll(Task.Run(async () =>
    {
        if (inputAsset != null)
        {
            WriteLine("Deleting input asset...");
            await inputAsset.DeleteAsync(WaitUntil.Completed);
        }
    }), Task.Run(async () =>
    {

        if (outputAsset != null)
        {
            WriteLine("Deleting output asset...");
            await outputAsset.DeleteAsync(WaitUntil.Completed);
        }
    }));

    WriteLine("All resources deleted.");
}

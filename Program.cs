using Azure;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Media;
using Azure.ResourceManager.Media.Models;
using Azure.Storage.Blobs;
using static System.Console;
using static System.Environment;

var OutputFolder = Path.Join(Environment.GetFolderPath(SpecialFolder.UserProfile), "Downloads", "output");
var InputFileName = Path.Join(Environment.GetFolderPath(SpecialFolder.UserProfile), "Downloads", "input.mp4");
var ContainerName = "files";
// var OutputContainerFolder = "optimized";
// var InputContainerFolder = "raw";

WriteLine("Starting app");

var AZURE_MEDIA_SERVICES_ACCOUNT_NAME = "mediamoonstar";
var AZURE_RESOURCE_GROUP = "streaming-example";
var AZURE_SUBSCRIPTION_ID = "02b34e38-f3f1-4d44-94ad-21d9a06d27ca";

var mediaServicesResourceId = MediaServicesAccountResource.CreateResourceIdentifier(AZURE_SUBSCRIPTION_ID, AZURE_RESOURCE_GROUP, AZURE_MEDIA_SERVICES_ACCOUNT_NAME);

var credentials = new DefaultAzureCredential(includeInteractiveCredentials: true);
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
    inputAsset = await CreateInputAssetAsync(mediaServicesAccount, ContainerName, inputAssetName, InputFileName);
    WriteLine("Creating output asset");
    outputAsset = await CreateOutputAssetAsync(mediaServicesAccount, ContainerName, outputAssetName);

    WriteLine("Executing transform...");
    job = await SubmitJobAsync(transform, jobName, inputAsset, outputAsset);
    job = await WaitForJobCompletionAsync(job);
}
finally
{
    if (job?.Data.State == MediaJobState.Finished && outputAsset is { })
    {
        WriteLine("Downloading results...");
        await DownloadOutputAsync(outputAsset, OutputFolder);
    }
    else
    {
        WriteLine("Job failed. Cleaning up...");
        await CleanUpJobAsync(transform, job, inputAsset, outputAsset);
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
                                    new H264Layer(bitrate: 1200000)
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

static async Task<MediaAssetResource> CreateInputAssetAsync(MediaServicesAccountResource account, string containerName, string assetName, string fileToUpload)
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
            new MediaAssetData
            {
                // Container = containerName,
                // StorageAccountName = "mediaexamplemoonstar",
            }
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

static async Task<MediaAssetResource> CreateOutputAssetAsync(MediaServicesAccountResource account, string containerName, string assetName)
{
    var asset = await account.GetMediaAssets().CreateOrUpdateAsync(
        WaitUntil.Completed,
        assetName,
        new MediaAssetData
        {
            // Container = containerName,
        }
    );

    // await MakeAssetPublicAsync(asset.Value);

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

async static Task DownloadOutputAsync(MediaAssetResource asset, string outputFolderName)
{
    var assetContainerSas = asset.GetStorageContainerUrisAsync(new MediaAssetStorageContainerSasContent
    {
        Permissions = MediaAssetContainerPermission.Read,
        ExpireOn = DateTime.UtcNow.AddHours(1),
    });

    var containerSasUrl = await assetContainerSas.FirstAsync();
    var container = new BlobContainerClient(containerSasUrl);

    var outputDir = Path.Combine(outputFolderName, asset.Data.Name);
    Directory.CreateDirectory(outputDir);

    WriteLine($"Downloading results to {outputDir}");

    await foreach (var blob in container.GetBlobsAsync())
    {
        var blobClient = container.GetBlobClient(blob.Name);
        var filename = Path.Combine(outputDir, blob.Name);
        await blobClient.DownloadToAsync(filename);
    }

    WriteLine("Download complete.");
}

async static Task CleanUpJobAsync(
    MediaTransformResource? transform,
    MediaJobResource? job,
    MediaAssetResource? inputAsset,
    MediaAssetResource? outputAsset)
{
    WriteLine("Cleaning up...");

    if (job != null)
    {
        WriteLine("Deleting job...");
        await job.DeleteAsync(WaitUntil.Completed);
    }

    if (transform != null)
    {
        WriteLine("Deleting transform...");
        await transform.DeleteAsync(WaitUntil.Completed);
    }

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

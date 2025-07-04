﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BambuVideoStream.Models;
using BambuVideoStream.Models.Mqtt;
using BambuVideoStream.Models.Wrappers;
using BambuVideoStream.Services;
using BambuVideoStream.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using Newtonsoft.Json;
using OBSWebsocketDotNet.Communication;
using OBSWebsocketDotNet.Types;
using static BambuVideoStream.Constants.OBS;

namespace BambuVideoStream;

/// <summary>
/// Main background service that connects to the Bambu MQTT server and processes messages to OBS
/// </summary>
public class BambuStreamBackgroundService : BackgroundService
{
    private readonly ILogger<BambuStreamBackgroundService> log;
    private readonly IHostApplicationLifetime hostLifetime;

    private readonly AppSettings appSettings;
    private readonly BambuSettings bambuSettings;
    private readonly OBSSettings obsSettings;

    private readonly IMqttClient mqttClient;
    private readonly MqttClientOptions mqttClientOptions;
    private readonly MqttClientSubscribeOptions mqttSubscribeOptions;
    private readonly MyOBSWebsocket obs;
    private readonly FtpService ftpService;
    private readonly ConcurrentQueue<Action> queuedOperations = new();

    private CancellationToken hostCancellationToken;
    private readonly SemaphoreSlim mqttReconnectionSemaphore = new(1);
    private readonly Channel<MqttApplicationMessageReceivedEventArgs> mqttProcessingChannel;

    private bool obsInitialized;

    private InputSettings chamberTemp;
    private InputSettings bedTemp;
    private InputSettings targetBedTemp;
    private InputSettings nozzleTemp;
    private InputSettings targetNozzleTemp;
    private InputSettings percentComplete;
    private InputSettings layers;
    private InputSettings timeRemaining;
    private InputSettings subtaskName;
    private InputSettings stage;
    private InputSettings partFan;
    private InputSettings auxFan;
    private InputSettings chamberFan;
    private InputSettings filament;
    private InputSettings printWeight;
    private ToggleIconInputSettings nozzleTempIcon;
    private ToggleIconInputSettings bedTempIcon;
    private ToggleIconInputSettings partFanIcon;
    private ToggleIconInputSettings auxFanIcon;
    private ToggleIconInputSettings chamberFanIcon;
    private ToggleIconInputSettings previewImage;

    private string subtask_name;
    private int lastLayerNum;
    private PrintStage? lastPrintStage;

    public BambuStreamBackgroundService(
        FtpService ftpService,
        MyOBSWebsocket obsWebsocket,
        IOptions<BambuSettings> bambuOptions,
        IOptions<OBSSettings> obsOptions,
        IOptions<AppSettings> appOptions,
        ILogger<BambuStreamBackgroundService> logger,
        ILogger<MqttClient> mqttLogger,
        IHostApplicationLifetime hostLifetime)
    {
        this.bambuSettings = bambuOptions.Value;
        this.obsSettings = obsOptions.Value;
        this.appSettings = appOptions.Value;

        this.obs = obsWebsocket;
        this.obs.Connected += this.Obs_Connected;
        this.obs.Disconnected += this.Obs_Disconnected;

        var mqttFactory = new MqttFactory(new MqttLogger(mqttLogger));
        this.mqttClient = mqttFactory.CreateMqttClient();
        this.mqttClient.ApplicationMessageReceivedAsync += this.OnMessageReceived;
        this.mqttClient.DisconnectedAsync += this.MqttClient_DisconnectedAsync;
        this.mqttClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(this.bambuSettings.IpAddress, this.bambuSettings.MqttPort)
            .WithCredentials(this.bambuSettings.Username, this.bambuSettings.Password)
            .WithTlsOptions(new MqttClientTlsOptions
            {
                UseTls = true,
                SslProtocol = SslProtocols.Tls12,
                CertificateValidationHandler = x => { return true; }
            })
            .Build();
        this.mqttSubscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f =>
            {
                f.WithTopic($"device/{this.bambuSettings.Serial}/report");
            }).Build();

        this.ftpService = ftpService;
        this.log = logger;
        this.hostLifetime = hostLifetime;
        this.mqttProcessingChannel = Channel.CreateBounded<MqttApplicationMessageReceivedEventArgs>(
            new BoundedChannelOptions(5) // Max 5 messages in queue
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
    }

    /// <summary>
    /// Called by the runtime to start the background service.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.hostCancellationToken = stoppingToken;
        this.obs.ConnectAsync(this.obsSettings.WsAddress, this.obsSettings.WsPassword ?? string.Empty);
        stoppingToken.Register(() => this.obs.Disconnect());

        var mqttFactory = new MqttFactory();

        using var _ = this.mqttClient;
        try
        {
            var connectResult = await this.mqttClient.ConnectAsync(this.mqttClientOptions, stoppingToken);
            if (connectResult?.ResultCode != MqttClientConnectResultCode.Success)
            {
                throw new Exception($"Failed to connect to Bambu MQTT: {connectResult.ResultCode}");
            }

            this.log.LogInformation("Connected to Bambu MQTT");

            await this.mqttClient.SubscribeAsync(this.mqttSubscribeOptions, stoppingToken);

            // Start processing messages
            Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in this.mqttProcessingChannel.Reader.ReadAllAsync(stoppingToken))
                    {
                        try
                        {
                            this.ProcessBambuMessage(e);
                            // Super small delay to prevent bombarding OBS
                            await Task.Delay(10, stoppingToken);
                        }
                        catch { } // Method logs all exceptions
                    }
                }
                catch (OperationCanceledException)
                { }
                catch (Exception ex)
                {
                    this.log.LogError(ex, "Unexpected error in reader thread");
                }
                finally
                {
                    this.log.LogDebug("Reader thread stopped");
                    this.hostLifetime.StopApplication();
                }
            }, stoppingToken).Forget();
            stoppingToken.Register(() => this.mqttProcessingChannel.Writer.Complete());

            // Wait for the application to stop
            var waitForClose = new TaskCompletionSource();
            stoppingToken.Register(() => waitForClose.SetResult());
            await waitForClose.Task;

            // shutting down
            await this.mqttClient.DisconnectAsync(cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            this.log.LogError(ex, "Bambu MQTT failure");
        }
        finally
        {
            this.hostLifetime.StopApplication();
        }
    }

    /// <summary>
    /// Called when the OBS Websocket connects, to initialize the scene and inputs
    /// </summary>
    private async void Obs_Connected(object sender, EventArgs e)
    {
        this.log.LogInformation("Connected to OBS WebSocket");

        if (this.appSettings.PrintSceneItemsAndExit)
        {
            this.PrintSceneItems();
            this.hostLifetime.StopApplication();
            return;
        }

        try
        {
            // ===========================================
            // Scene and video stream
            // ===========================================
            await this.obs.EnsureVideoSettingsAsync(this.hostCancellationToken);
            await this.obs.EnsureBambuSceneAsync(this.hostCancellationToken);
            await this.obs.EnsureBambuStreamSourceAsync(this.hostCancellationToken);
            await this.obs.EnsureColorSourceAsync(this.hostCancellationToken);

            // Z-index for inputs starts at 2 (will be incremented below), because the stream and color source are at 0 and 1
            int z_index = 2;

            // ===========================================
            // Text sources
            // ===========================================
            this.chamberTemp = await this.obs.EnsureTextInputAsync(ChamberTempInitialSettings, z_index++, this.hostCancellationToken);
            this.bedTemp = await this.obs.EnsureTextInputAsync(BedTempInitialSettings, z_index++, this.hostCancellationToken);
            this.targetBedTemp = await this.obs.EnsureTextInputAsync(TargetBedTempInitialSettings, z_index++, this.hostCancellationToken);
            this.nozzleTemp = await this.obs.EnsureTextInputAsync(NozzleTempInitialSettings, z_index++, this.hostCancellationToken);
            this.targetNozzleTemp = await this.obs.EnsureTextInputAsync(TargetNozzleTempInitialSettings, z_index++, this.hostCancellationToken);
            this.percentComplete = await this.obs.EnsureTextInputAsync(PercentCompleteInitialSettings, z_index++, this.hostCancellationToken);
            this.layers = await this.obs.EnsureTextInputAsync(LayersInitialSettings, z_index++, this.hostCancellationToken);
            this.timeRemaining = await this.obs.EnsureTextInputAsync(TimeRemainingInitialSettings, z_index++, this.hostCancellationToken);
            this.subtaskName = await this.obs.EnsureTextInputAsync(SubtaskNameInitialSettings, z_index++, this.hostCancellationToken);
            this.stage = await this.obs.EnsureTextInputAsync(StageInitialSettings, z_index++, this.hostCancellationToken);
            this.partFan = await this.obs.EnsureTextInputAsync(PartFanInitialSettings, z_index++, this.hostCancellationToken);
            this.auxFan = await this.obs.EnsureTextInputAsync(AuxFanInitialSettings, z_index++, this.hostCancellationToken);
            this.chamberFan = await this.obs.EnsureTextInputAsync(ChamberFanInitialSettings, z_index++, this.hostCancellationToken);
            this.filament = await this.obs.EnsureTextInputAsync(FilamentInitialSettings, z_index++, this.hostCancellationToken);
            this.printWeight = await this.obs.EnsureTextInputAsync(PrintWeightInitialSettings, z_index++, this.hostCancellationToken);

            // ===========================================
            // Image sources
            // ===========================================
            this.nozzleTempIcon = await this.obs.EnsureImageInputAsync(NozzleTempIconInitialSettings, z_index++, this.hostCancellationToken);
            this.bedTempIcon = await this.obs.EnsureImageInputAsync(BedTempIconInitialSettings, z_index++, this.hostCancellationToken);
            this.partFanIcon = await this.obs.EnsureImageInputAsync(PartFanIconInitialSettings, z_index++, this.hostCancellationToken);
            this.auxFanIcon = await this.obs.EnsureImageInputAsync(AuxFanIconInitialSettings, z_index++, this.hostCancellationToken);
            this.chamberFanIcon = await this.obs.EnsureImageInputAsync(ChamberFanIconInitialSettings, z_index++, this.hostCancellationToken);
            this.previewImage = await this.obs.EnsureImageInputAsync(PreviewImageInitialSettings, z_index++, this.hostCancellationToken);
            // Static image sources
            await this.obs.EnsureImageInputAsync(ChamberTempIconInitialSettings, z_index++, this.hostCancellationToken);
            await this.obs.EnsureImageInputAsync(TimeIconInitialSettings, z_index++, this.hostCancellationToken);
            await this.obs.EnsureImageInputAsync(FilamentIconInitialSettings, z_index++, this.hostCancellationToken);


            this.obsInitialized = true;

            if (this.obsSettings.StartStreamOnStartup && !this.obs.GetStreamStatus().IsActive)
            {
                this.obs.StartStream();
            }
        }
        catch (OperationCanceledException)
        {
            // Do nothing. This is expected when the service is shutting down.
        }
        catch (Exception ex)
        {
            this.log.LogError(ex, "Failed to initialize OBS inputs. Is your OBS Studio set up correctly?");
            this.hostLifetime.StopApplication();
        }
    }

    /// <summary>
    /// Called when the OBS Websocket disconnects/fails to connect
    /// </summary>
    private void Obs_Disconnected(object sender, ObsDisconnectionInfo e)
    {
        this.obsInitialized = false;
        this.log.LogWarning("OBS WebSocket disconnected: {reason} ({opcode})", e.DisconnectReason, e.ObsCloseCode);

        if (e.ObsCloseCode == ObsCloseCodes.AuthenticationFailed)
        {
            this.log.LogError("OBS WebSocket authentication failed. Check your OBS settings.");
            this.hostLifetime.StopApplication();
            return;
        }

        if (this.appSettings.ExitOnObsDisconnect)
        {
            this.hostLifetime.StopApplication();
        }
        else
        {
            // Reconnection will happen in background by OBSWebsocket
            this.log.LogWarning("Waiting for OBS reconnection...");
        }
    }

    /// <summary>
    /// Called when the Bambu MQTT client disconnects
    /// </summary>
    private async Task MqttClient_DisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        this.log.LogWarning("Bambu MQTT disconnected: {reasonstring} ({reason})", arg.ReasonString, arg.Reason);

        if (!await this.mqttReconnectionSemaphore.WaitAsync(0))
        {
            // Another thread is already reconnecting
            return;
        }
        try
        {
            if (arg.Reason == MqttClientDisconnectReason.NotAuthorized)
            {
                this.log.LogError("Bambu MQTT authentication failed. Check your Bambu settings.");
                this.hostLifetime.StopApplication();
                return;
            }

            if (this.appSettings.ExitOnBambuDisconnect)
            {
                this.hostLifetime.StopApplication();
            }
            else
            {
                this.log.LogWarning("Waiting for Bambu MQTT reconnection...");
                while (!this.mqttClient.IsConnected && !this.hostCancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await this.mqttClient.ReconnectAsync(this.hostCancellationToken);
                        if (!this.mqttClient.IsConnected)
                        {
                            await Task.Delay(1000, this.hostCancellationToken);
                        }
                    }
                    catch (Exception e)
                    {
                        this.log.LogDebug(e, "Failed to reconnect to Bambu MQTT");
                    }
                }
            }
        }
        finally
        {
            this.mqttReconnectionSemaphore.Release();
        }
    }

    /// <summary>
    /// Message receiver callback. Adds the message to the channel for processing.
    /// </summary>
    private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        await this.mqttProcessingChannel.Writer.WriteAsync(e, this.hostCancellationToken);
    }

    /// <summary>
    /// Main Bambu MQTT message processing method. Called by the channel reader.
    /// </summary>
    /// <remarks>
    /// Catches all exceptions.
    /// </remarks>
    private void ProcessBambuMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            string json = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            this.log.LogTrace("Received message: {json}", json);

            using var doc = JsonDocument.Parse(json);

            var root = doc.RootElement.EnumerateObject().Select(x => x.Name).First();

            switch (root)
            {
                case "print":
                    this.log.LogTrace("Received 'print' message");

                    var p = doc.Deserialize<PrintMessage>();

                    if (!this.obs.IsConnected || !this.obsInitialized || !p.IsStatusUpdateMessage())
                    {
                        // Not a status update command, skip
                        break;
                    }

                    obs.UpdateText(this.chamberTemp, $"{p.print.chamber_temper} °C");
                    obs.UpdateText(this.bedTemp, $"{p.print.bed_temper}");

                    obs.SetIconState(this.bedTempIcon, p.print.bed_target_temper > 0);
                    obs.SetIconState(this.nozzleTempIcon, p.print.nozzle_target_temper > 0);

                    string targetBedTempStr = $" / {p.print.bed_target_temper} °C";
                    if (p.print.bed_target_temper == 0)
                    {
                        targetBedTempStr = "";
                    }

                    obs.UpdateText(this.targetBedTemp, targetBedTempStr);
                    obs.UpdateText(this.nozzleTemp, $"{p.print.nozzle_temper}");

                    string targetNozzleTempStr = $" / {p.print.nozzle_target_temper} °C";
                    if (p.print.nozzle_target_temper == 0)
                    {
                        targetNozzleTempStr = "";
                    }

                    obs.UpdateText(this.targetNozzleTemp, targetNozzleTempStr);

                    string percentMsg = $"{p.print.mc_percent}% complete";
                    obs.UpdateText(this.percentComplete, percentMsg);
                    string layerMsg = $"Layers: {p.print.layer_num}/{p.print.total_layer_num}";
                    obs.UpdateText(this.layers, layerMsg);

                    if (this.lastLayerNum != p.print.layer_num)
                    {
                        this.log.LogInformation("{percentMsg}: {layerMsg}", percentMsg, layerMsg);
                        this.lastLayerNum = p.print.layer_num;
                    }

                    var time = TimeSpan.FromMinutes(p.print.mc_remaining_time);
                    string timeFormatted = "";
                    if (time.TotalMinutes > 59)
                    {
                        timeFormatted = string.Format("-{0}h{1}m", (int)time.TotalHours, time.Minutes);
                    }
                    else
                    {
                        timeFormatted = string.Format("-{0}m", time.Minutes);
                    }

                    obs.UpdateText(this.timeRemaining, timeFormatted);
                    obs.UpdateText(this.subtaskName, $"Model: {p.print.subtask_name}");
                    obs.UpdateText(this.stage, $"Stage: {p.print.current_stage_str}");

                    obs.UpdateText(this.partFan, $"Part: {p.print.GetFanSpeed(p.print.cooling_fan_speed)}%");
                    obs.UpdateText(this.auxFan, $"Aux: {p.print.GetFanSpeed(p.print.big_fan1_speed)}%");
                    obs.UpdateText(this.chamberFan, $"Chamber: {p.print.GetFanSpeed(p.print.big_fan2_speed)}%");

                    obs.SetIconState(this.partFanIcon, p.print.cooling_fan_speed != "0");
                    obs.SetIconState(this.auxFanIcon, p.print.big_fan1_speed != "0");
                    obs.SetIconState(this.chamberFanIcon, p.print.big_fan2_speed != "0");

                    var tray = p.print.ams?.GetCurrentTray();
                    if (tray != null)
                    {
                        obs.UpdateText(this.filament, tray.tray_type);
                    }

                    if (!string.IsNullOrEmpty(p.print.subtask_name) && p.print.subtask_name != this.subtask_name)
                    {
                        this.subtask_name = p.print.subtask_name;
                        var fileLocation = this.DetermineFileLocation(this.subtask_name);
                        if (!string.IsNullOrEmpty(fileLocation))
                        {
                            this.DownloadFileImagePreview(fileLocation);

                            var weight = this.ftpService.GetPrintJobWeight(fileLocation);
                            obs.UpdateText(this.printWeight, $"{weight}g");
                        }
                        else
                        {
                            this.log.LogWarning("Image preview and print weight unavailable.");
                        }
                    }

                    this.CheckStreamStatus(p);

                    break;

                default:
                    this.log.LogTrace("Unknown message type: {root}", root);
                    break;
            }
        }
        catch (Exception err) when (err is ObjectDisposedException or OperationCanceledException)
        {
            // Do nothing. This is expected when the service is shutting down.
        }
        catch (NullReferenceException ex) when (ex.Message == "Websocket is not initialized")
        {
            // Do nothing. This is expected when OBS is not connected or got disposed while processing a message.
        }
        catch (Exception ex)
        {
            this.log.LogError(ex, "Failed to process message");
        }
    }

    private string DetermineFileLocation(string subtaskName)
    {
        var cacheFileName = $"/cache/{subtaskName.Trim()}".EnsureSuffix(".3mf"); // File was sent via BambuStudio
        var savedFileName = $"/{subtaskName.Trim()}".EnsureSuffix(".3mf"); // File was saved to the printer
        if (this.ftpService.FileExists(cacheFileName))
        {
            this.log.LogInformation("Found print file at: {cacheFileName}", cacheFileName);
            return cacheFileName;
        }
        else if (this.ftpService.FileExists(savedFileName))
        {
            this.log.LogInformation("Found print file at: {savedFileName}", savedFileName);
            return savedFileName;
        }

        this.log.LogWarning("Couldn't find location of print file '{subtask_name}.3mf'", subtaskName);
        return null;
    }

    private void DownloadFileImagePreview(string fileName)
    {
        using var op = this.log.BeginScope(nameof(DownloadFileImagePreview));
        this.log.LogInformation("getting {fileName} from ftp", fileName);
        try
        {
            var bytes = this.ftpService.GetFileThumbnail(fileName);

            File.WriteAllBytes(PreviewImageInitialSettings.DefaultEnabledIconPath, bytes);
            this.log.LogInformation("got image preview");

            this.obs.SetIconState(this.previewImage, true);
            this.log.LogInformation("updated image preview");
        }
        catch (Exception ex)
        {
            this.log.LogError(ex, "Failed to get image preview");
        }
    }

    /// <summary>
    /// Checks the status of the obs stream and stops it if the print is complete
    /// </summary>
    /// <param name="p">The PrintMessage from MQTT</param>
    private void CheckStreamStatus(PrintMessage p)
    {
        try
        {
            if (p.print.current_stage == PrintStage.Idle &&
                this.lastPrintStage != null &&
                this.lastPrintStage != PrintStage.Idle)
            {
                this.log.LogInformation("Print complete!");
            }

            // Stop stream?
            if (this.queuedOperations.IsEmpty)
            {
                if (p.print.current_stage == PrintStage.Idle &&
                    this.obs.GetStreamStatus().IsActive &&
                    this.obsSettings.StopStreamOnPrinterIdle)
                {
                    this.log.LogInformation("Stopping stream in 5s");
                    this.queuedOperations.Enqueue(() =>
                    {
                        // Must check again - StopStream() throws if stream already stopped
                        if (this.obs.GetStreamStatus().IsActive)
                        {
                            this.obs.StopStream();
                        }
                    });
                }

                // Check for app shutdown conditions
                if (p.print.current_stage == PrintStage.Idle &&
                    this.appSettings.ExitOnIdle)
                {
                    this.log.LogInformation("Printer is idle. Exiting in 5s.");
                    this.queuedOperations.Enqueue(() => this.hostLifetime.StopApplication());
                }

                if (!this.queuedOperations.IsEmpty)
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(5000, this.hostCancellationToken);
                        while (this.queuedOperations.TryDequeue(out var action))
                        {
                            action();
                            await Task.Delay(250, this.hostCancellationToken);
                        }
                    });
                }
            }

            // Start stream?
            if (this.queuedOperations.IsEmpty &&
                p.print.current_stage != PrintStage.Idle &&
                this.obsSettings.StartStreamOnStartup &&
                !this.obs.GetStreamStatus().IsActive)
            {
                this.log.LogInformation("Printer has resumed printing. Starting stream.");
                this.obs.StartStream();
            }
        }
        finally
        {
            this.lastPrintStage = p.print.current_stage;
        }
    }

    /// <summary>
    /// Utility method for getting scene items
    /// </summary>
    private void PrintSceneItems()
    {
        this.log.LogInformation("Video settings:\n{settings}", JsonConvert.SerializeObject(this.obs.GetVideoSettings(), Formatting.Indented));

        var list = this.obs.GetInputList();

        foreach (var input in list)
        {
            string scene = this.obsSettings.BambuScene;
            string source = input.InputName;

            try
            {
                int itemId = this.obs.GetSceneItemId(scene, source, 0);
                var transform = this.obs.GetSceneItemTransform(scene, itemId);
                var settings = this.obs.GetInputSettings(source);
                this.log.LogInformation("{inputKind} {source}:\n{transform}\nSettings:\n{settings}", input.InputKind, source, JsonConvert.SerializeObject(transform, Formatting.Indented), settings.Settings.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                this.log.LogTrace(ex, "Failed to get scene item {source}", source);
            }
        }
    }
}


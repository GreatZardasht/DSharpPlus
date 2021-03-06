﻿#pragma warning disable CS0618
using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using DSharpPlus.Net;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;

namespace DSharpPlus
{
    /// <summary>
    /// A Discord client that shards automatically.
    /// </summary>
    public sealed partial class DiscordShardedClient
    {
        #region Public Properties

        /// <summary>
        /// Gets the logger for this client.
        /// </summary>
        public ILogger<BaseDiscordClient> Logger { get; }

        /// <summary>
        /// Gets all client shards.
        /// </summary>
        public IReadOnlyDictionary<int, DiscordClient> ShardClients { get; }

        /// <summary>
        /// Gets the gateway info for the client's session.
        /// </summary>
        public GatewayInfo GatewayInfo { get; private set; }

        /// <summary>
        /// Gets the current user.
        /// </summary>
        public DiscordUser CurrentUser { get; private set; }

        /// <summary>
        /// Gets the current application.
        /// </summary>
        public DiscordApplication CurrentApplication { get; private set; }

        /// <summary>
        /// Gets the list of available voice regions. Note that this property will not contain VIP voice regions.
        /// </summary>
        public IReadOnlyDictionary<string, DiscordVoiceRegion> VoiceRegions 
            => this._voiceRegionsLazy?.Value;

        #endregion

        #region Private Properties/Fields

        private DiscordConfiguration Configuration { get; }

        /// <summary>
        /// Gets the list of available voice regions. This property is meant as a way to modify <see cref="VoiceRegions"/>.
        /// </summary>
        private ConcurrentDictionary<string, DiscordVoiceRegion> _internalVoiceRegions;

        private ConcurrentDictionary<int, DiscordClient> _shards = new ConcurrentDictionary<int, DiscordClient>();
        private Lazy<IReadOnlyDictionary<string, DiscordVoiceRegion>> _voiceRegionsLazy;
        private bool _isStarted = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes new auto-sharding Discord client.
        /// </summary>
        /// <param name="config">Configuration to use.</param>
        public DiscordShardedClient(DiscordConfiguration config)
        {
            this.InternalSetup();

            this.Configuration = config;
            this.ShardClients = new ReadOnlyConcurrentDictionary<int, DiscordClient>(this._shards);

            if (this.Configuration.LoggerFactory == null)
            {
                this.Configuration.LoggerFactory = new DefaultLoggerFactory();
                this.Configuration.LoggerFactory.AddProvider(new DefaultLoggerProvider(this.Configuration.MinimumLogLevel, this.Configuration.LogTimestampFormat));
            }
            this.Logger = this.Configuration.LoggerFactory.CreateLogger<BaseDiscordClient>();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Initializes and connects all shards.
        /// </summary>
        /// <exception cref="AggregateException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        /// <returns></returns>
        public async Task StartAsync()
        {
            if (this._isStarted)
                throw new InvalidOperationException("This client has already been started.");

            this._isStarted = true;

            try
            {
                if (this.Configuration.TokenType != TokenType.Bot)
                    this.Logger.LogWarning(LoggerEvents.Misc, "You are logging in with a token that is not a bot token. This is not officially supported by Discord, and can result in your account being terminated if you aren't careful.");
                this.Logger.LogInformation(LoggerEvents.Startup, "DSharpPlus, version {0}", this._versionString.Value);

                var shardc = await this.InitializeShardsAsync().ConfigureAwait(false);
                var connectTasks = new List<Task>();
                this.Logger.LogInformation(LoggerEvents.ShardStartup, "Booting {0} shards.", shardc);

                for (var i = 0; i < shardc; i++)
                {
                    //This should never happen, but in case it does...
                    if (this.GatewayInfo.SessionBucket.MaxConcurrency < 1)
                        this.GatewayInfo.SessionBucket.MaxConcurrency = 1;

                    if (this.GatewayInfo.SessionBucket.MaxConcurrency == 1)
                        await this.ConnectShardAsync(i).ConfigureAwait(false);
                    else
                    {
                        //Concurrent login.
                        connectTasks.Add(this.ConnectShardAsync(i));

                        if (connectTasks.Count == this.GatewayInfo.SessionBucket.MaxConcurrency)
                        {
                            await Task.WhenAll(connectTasks).ConfigureAwait(false);
                            connectTasks.Clear();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await this.InternalStopAsync(false).ConfigureAwait(false);

                var message = $"Shard initialization failed, check inner exceptions for details: ";

                this.Logger.LogCritical(LoggerEvents.ShardClientError, $"{message}\n{ex}");
                throw new AggregateException(message, ex);
            }
        }
        /// <summary>
        /// Disconnects and disposes of all shards.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public Task StopAsync()
            => this.InternalStopAsync();

        /// <summary>
        /// Gets a shard from a guild id.
        /// <para>This method uses the <see cref="Utilities.GetShardId(ulong, int)"/> method and will not iterate through the shard guild caches.</para>
        /// </summary>
        /// <param name="guildId">The guild id for the shard.</param>
        /// <returns>The found <see cref="DiscordClient"/> shard.</returns>
        public DiscordClient GetShard(ulong guildId)
        {
            var index = Utilities.GetShardId(guildId, this.ShardClients.Count);
            return this._shards[index];
        }

        /// <summary>
        /// Gets a shard from a guild.
        /// <para>This method uses the <see cref="Utilities.GetShardId(ulong, int)"/> method and will not iterate through the shard guild caches.</para>
        /// </summary>
        /// <param name="guild">The guild for the shard.</param>
        /// <returns>The found <see cref="DiscordClient"/> shard.</returns>
        public DiscordClient GetShard(DiscordGuild guild)
            => this.GetShard(guild.Id);

        /// <summary>
        /// Updates playing statuses on all shards.
        /// </summary>
        /// <param name="activity">Activity to set.</param>
        /// <param name="userStatus">Status of the user.</param>
        /// <param name="idleSince">Since when is the client performing the specified activity.</param>
        /// <returns>Asynchronous operation.</returns>
        public async Task UpdateStatusAsync(DiscordActivity activity = null, UserStatus? userStatus = null, DateTimeOffset? idleSince = null)
        {
            var tasks = new List<Task>();
            foreach (var client in this._shards.Values)
                tasks.Add(client.UpdateStatusAsync(activity, userStatus, idleSince));

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        #endregion

        #region Internal Methods

        internal async Task<int> InitializeShardsAsync()
        {
            if (this._shards.Count != 0)
                return this._shards.Count;

            this.GatewayInfo = await this.GetGatewayInfoAsync().ConfigureAwait(false);
            var shardc = this.Configuration.ShardCount == 1 ? this.GatewayInfo.ShardCount : this.Configuration.ShardCount;
            var lf = new ShardedLoggerFactory(this.Logger);
            for (var i = 0; i < shardc; i++)
            {
                var cfg = new DiscordConfiguration(this.Configuration)
                {
                    ShardId = i,
                    ShardCount = shardc,
                    LoggerFactory = lf
                };

                var client = new DiscordClient(cfg);
                if (!this._shards.TryAdd(i, client))
                    throw new InvalidOperationException("Could not initialize shards.");
            }

            return shardc;
        }

        #endregion

        #region Private Methods/Version Property

        private async Task<GatewayInfo> GetGatewayInfoAsync()
        {
            var url = $"{Utilities.GetApiBaseUri()}{Endpoints.GATEWAY}{Endpoints.BOT}";
            var http = new HttpClient();

            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Utilities.GetUserAgent());
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", Utilities.GetFormattedToken(this.Configuration));

            this.Logger.LogDebug(LoggerEvents.ShardRest, $"Obtaining gateway information from GET {Endpoints.GATEWAY}{Endpoints.BOT}...");
            var resp = await http.GetAsync(url).ConfigureAwait(false);
            
            http.Dispose();

            if (!resp.IsSuccessStatusCode)
            {
                var ratelimited = await HandleHttpError(url, resp).ConfigureAwait(false);

                if (ratelimited)
                    return await this.GetGatewayInfoAsync().ConfigureAwait(false);
            }

            var timer = new Stopwatch();
            timer.Start();

            var jo = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var info = jo.ToObject<GatewayInfo>();

            //There is a delay from parsing here.
            timer.Stop();

            info.SessionBucket.resetAfter -= (int)timer.ElapsedMilliseconds; 
            info.SessionBucket.ResetAfter = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(info.SessionBucket.resetAfter);

            return info;

            async Task<bool> HandleHttpError(string reqUrl, HttpResponseMessage msg)
            {
                var code = (int)msg.StatusCode;

                if (code == 401 || code == 403)
                {
                    throw new Exception($"Authentication failed, check your token and try again: {code} {msg.ReasonPhrase}");
                }
                else if (code == 429)
                {
                    this.Logger.LogError(LoggerEvents.ShardClientError, $"Ratelimit hit, requeuing request to {reqUrl}");

                    var hs = msg.Headers.ToDictionary(xh => xh.Key, xh => string.Join("\n", xh.Value), StringComparer.OrdinalIgnoreCase);
                    var waitInterval = 0;

                    if (hs.TryGetValue("Retry-After", out var retry_after_raw))
                        waitInterval = int.Parse(retry_after_raw, CultureInfo.InvariantCulture);

                    await Task.Delay(waitInterval).ConfigureAwait(false);
                    return true;
                }
                else if (code >= 500)
                {
                    throw new Exception($"Internal Server Error: {code} {msg.ReasonPhrase}");
                }
                else
                {
                    throw new Exception($"An unsuccessful HTTP status code was encountered: {code} {msg.ReasonPhrase}");
                }
            }
        }


        private readonly Lazy<string> _versionString = new Lazy<string>(() =>
        {
            var a = typeof(DiscordShardedClient).GetTypeInfo().Assembly;

            var iv = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (iv != null)
                return iv.InformationalVersion;

            var v = a.GetName().Version;
            var vs = v.ToString(3);

            if (v.Revision > 0)
                vs = $"{vs}, CI build {v.Revision}";

            return vs;
        });

        #endregion

        #region Private Connection Methods

        private async Task ConnectShardAsync(int i)
        {
            if (!this._shards.TryGetValue(i, out var client))
                throw new Exception($"Could not initialize shard {i}.");

            if (this.GatewayInfo != null)
            {
                client.GatewayInfo = this.GatewayInfo;
                client.GatewayUri = new Uri(client.GatewayInfo.Url);
            }

            if (this.CurrentUser != null)
                client.CurrentUser = this.CurrentUser;

            if (this.CurrentApplication != null)
                client.CurrentApplication = this.CurrentApplication;

            if (this._internalVoiceRegions != null)
            {
                client.InternalVoiceRegions = this._internalVoiceRegions;
                client._voice_regions_lazy = new Lazy<IReadOnlyDictionary<string, DiscordVoiceRegion>>(() => new ReadOnlyDictionary<string, DiscordVoiceRegion>(client.InternalVoiceRegions));
            }

            this.HookEventHandlers(client);

            client._isShard = true;
            await client.ConnectAsync().ConfigureAwait(false);
            this.Logger.LogInformation(LoggerEvents.ShardStartup, "Booted shard {0}.", i);

            if (this.CurrentUser == null)
                this.CurrentUser = client.CurrentUser;

            if (this.CurrentApplication == null)
                this.CurrentApplication = client.CurrentApplication;

            if (this._internalVoiceRegions == null)
            {
                this._internalVoiceRegions = client.InternalVoiceRegions;
                this._voiceRegionsLazy = new Lazy<IReadOnlyDictionary<string, DiscordVoiceRegion>>(() => new ReadOnlyDictionary<string, DiscordVoiceRegion>(this._internalVoiceRegions));
            }
        }

        private Task InternalStopAsync(bool enableLogger = true)
        {
            if (!this._isStarted)
                throw new InvalidOperationException("This client has not been started.");

            if (enableLogger)
                this.Logger.LogInformation(LoggerEvents.ShardShutdown, "Disposing {0} shards.", this._shards.Count);

            this._isStarted = false;
            this._voiceRegionsLazy = null;

            this.GatewayInfo = null;
            this.CurrentUser = null;
            this.CurrentApplication = null;

            for (int i = 0; i < this._shards.Count; i++)
            {
                if (this._shards.TryGetValue(i, out var client))
                {
                    this.UnhookEventHandlers(client);

                    client.Dispose();

                    if (enableLogger)
                        this.Logger.LogInformation(LoggerEvents.ShardShutdown, "Disconnected shard {0}.", i);
                }
            }

            this._shards.Clear();

            return Task.CompletedTask;
        }

        #endregion

        #region Event Handler Initialization/Registering

        private void InternalSetup()
        {
            this._clientErrored = new AsyncEvent<ClientErrorEventArgs>(this.Goof, "CLIENT_ERRORED");
            this._socketErrored = new AsyncEvent<SocketErrorEventArgs>(this.Goof, "SOCKET_ERRORED");
            this._socketOpened = new AsyncEvent(this.EventErrorHandler, "SOCKET_OPENED");
            this._socketClosed = new AsyncEvent<SocketCloseEventArgs>(this.EventErrorHandler, "SOCKET_CLOSED");
            this._ready = new AsyncEvent<ReadyEventArgs>(this.EventErrorHandler, "READY");
            this._resumed = new AsyncEvent<ReadyEventArgs>(this.EventErrorHandler, "RESUMED");
            this._channelCreated = new AsyncEvent<ChannelCreateEventArgs>(this.EventErrorHandler, "CHANNEL_CREATED");
            this._dmChannelCreated = new AsyncEvent<DmChannelCreateEventArgs>(this.EventErrorHandler, "DM_CHANNEL_CREATED");
            this._channelUpdated = new AsyncEvent<ChannelUpdateEventArgs>(this.EventErrorHandler, "CHANNEL_UPDATED");
            this._channelDeleted = new AsyncEvent<ChannelDeleteEventArgs>(this.EventErrorHandler, "CHANNEL_DELETED");
            this._dmChannelDeleted = new AsyncEvent<DmChannelDeleteEventArgs>(this.EventErrorHandler, "DM_CHANNEL_DELETED");
            this._channelPinsUpdated = new AsyncEvent<ChannelPinsUpdateEventArgs>(this.EventErrorHandler, "CHANNEL_PINS_UPDATED");
            this._guildCreated = new AsyncEvent<GuildCreateEventArgs>(this.EventErrorHandler, "GUILD_CREATED");
            this._guildAvailable = new AsyncEvent<GuildCreateEventArgs>(this.EventErrorHandler, "GUILD_AVAILABLE");
            this._guildUpdated = new AsyncEvent<GuildUpdateEventArgs>(this.EventErrorHandler, "GUILD_UPDATED");
            this._guildDeleted = new AsyncEvent<GuildDeleteEventArgs>(this.EventErrorHandler, "GUILD_DELETED");
            this._guildUnavailable = new AsyncEvent<GuildDeleteEventArgs>(this.EventErrorHandler, "GUILD_UNAVAILABLE");
            this._guildDownloadCompleted = new AsyncEvent<GuildDownloadCompletedEventArgs>(this.EventErrorHandler, "GUILD_DOWNLOAD_COMPLETED");
            this._inviteCreated = new AsyncEvent<InviteCreateEventArgs>(this.EventErrorHandler, "INVITE_CREATED");
            this._inviteDeleted = new AsyncEvent<InviteDeleteEventArgs>(this.EventErrorHandler, "INVITE_DELETED");
            this._messageCreated = new AsyncEvent<MessageCreateEventArgs>(this.EventErrorHandler, "MESSAGE_CREATED");
            this._presenceUpdated = new AsyncEvent<PresenceUpdateEventArgs>(this.EventErrorHandler, "PRESENCE_UPDATED");
            this._guildBanAdded = new AsyncEvent<GuildBanAddEventArgs>(this.EventErrorHandler, "GUILD_BAN_ADDED");
            this._guildBanRemoved = new AsyncEvent<GuildBanRemoveEventArgs>(this.EventErrorHandler, "GUILD_BAN_REMOVED");
            this._guildEmojisUpdated = new AsyncEvent<GuildEmojisUpdateEventArgs>(this.EventErrorHandler, "GUILD_EMOJI_UPDATED");
            this._guildIntegrationsUpdated = new AsyncEvent<GuildIntegrationsUpdateEventArgs>(this.EventErrorHandler, "GUILD_INTEGRATIONS_UPDATED");
            this._guildMemberAdded = new AsyncEvent<GuildMemberAddEventArgs>(this.EventErrorHandler, "GUILD_MEMBER_ADDED");
            this._guildMemberRemoved = new AsyncEvent<GuildMemberRemoveEventArgs>(this.EventErrorHandler, "GUILD_MEMBER_REMOVED");
            this._guildMemberUpdated = new AsyncEvent<GuildMemberUpdateEventArgs>(this.EventErrorHandler, "GUILD_MEMBER_UPDATED");
            this._guildRoleCreated = new AsyncEvent<GuildRoleCreateEventArgs>(this.EventErrorHandler, "GUILD_ROLE_CREATED");
            this._guildRoleUpdated = new AsyncEvent<GuildRoleUpdateEventArgs>(this.EventErrorHandler, "GUILD_ROLE_UPDATED");
            this._guildRoleDeleted = new AsyncEvent<GuildRoleDeleteEventArgs>(this.EventErrorHandler, "GUILD_ROLE_DELETED");
            this._messageUpdated = new AsyncEvent<MessageUpdateEventArgs>(this.EventErrorHandler, "MESSAGE_UPDATED");
            this._messageDeleted = new AsyncEvent<MessageDeleteEventArgs>(this.EventErrorHandler, "MESSAGE_DELETED");
            this._messageBulkDeleted = new AsyncEvent<MessageBulkDeleteEventArgs>(this.EventErrorHandler, "MESSAGE_BULK_DELETED");
            this._typingStarted = new AsyncEvent<TypingStartEventArgs>(this.EventErrorHandler, "TYPING_STARTED");
            this._userSettingsUpdated = new AsyncEvent<UserSettingsUpdateEventArgs>(this.EventErrorHandler, "USER_SETTINGS_UPDATED");
            this._userUpdated = new AsyncEvent<UserUpdateEventArgs>(this.EventErrorHandler, "USER_UPDATED");
            this._voiceStateUpdated = new AsyncEvent<VoiceStateUpdateEventArgs>(this.EventErrorHandler, "VOICE_STATE_UPDATED");
            this._voiceServerUpdated = new AsyncEvent<VoiceServerUpdateEventArgs>(this.EventErrorHandler, "VOICE_SERVER_UPDATED");
            this._guildMembersChunk = new AsyncEvent<GuildMembersChunkEventArgs>(this.EventErrorHandler, "GUILD_MEMBERS_CHUNKED");
            this._unknownEvent = new AsyncEvent<UnknownEventArgs>(this.EventErrorHandler, "UNKNOWN_EVENT");
            this._messageReactionAdded = new AsyncEvent<MessageReactionAddEventArgs>(this.EventErrorHandler, "MESSAGE_REACTION_ADDED");
            this._messageReactionRemoved = new AsyncEvent<MessageReactionRemoveEventArgs>(this.EventErrorHandler, "MESSAGE_REACTION_REMOVED");
            this._messageReactionsCleared = new AsyncEvent<MessageReactionsClearEventArgs>(this.EventErrorHandler, "MESSAGE_REACTIONS_CLEARED");
            this._messageReactionRemovedEmoji = new AsyncEvent<MessageReactionRemoveEmojiEventArgs>(this.EventErrorHandler, "MESSAGE_REACTION_REMOVED_EMOJI");
            this._webhooksUpdated = new AsyncEvent<WebhooksUpdateEventArgs>(this.EventErrorHandler, "WEBHOOKS_UPDATED");
            this._heartbeated = new AsyncEvent<HeartbeatEventArgs>(this.EventErrorHandler, "HEARTBEATED");
        }

        private void HookEventHandlers(DiscordClient client)
        {
            client.ClientErrored += this.Client_ClientError;
            client.SocketErrored += this.Client_SocketError;
            client.SocketOpened += this.Client_SocketOpened;
            client.SocketClosed += this.Client_SocketClosed;
            client.Ready += this.Client_Ready;
            client.Resumed += this.Client_Resumed;
            client.ChannelCreated += this.Client_ChannelCreated;
            client.DmChannelCreated += this.Client_DMChannelCreated;
            client.ChannelUpdated += this.Client_ChannelUpdated;
            client.ChannelDeleted += this.Client_ChannelDeleted;
            client.DmChannelDeleted += this.Client_DMChannelDeleted;
            client.ChannelPinsUpdated += this.Client_ChannelPinsUpdated;
            client.GuildCreated += this.Client_GuildCreated;
            client.GuildAvailable += this.Client_GuildAvailable;
            client.GuildUpdated += this.Client_GuildUpdated;
            client.GuildDeleted += this.Client_GuildDeleted;
            client.GuildUnavailable += this.Client_GuildUnavailable;
            client.GuildDownloadCompleted += this.Client_GuildDownloadCompleted;
            client.InviteCreated += this.Client_InviteCreated;
            client.InviteDeleted += this.Client_InviteDeleted;
            client.MessageCreated += this.Client_MessageCreated;
            client.PresenceUpdated += this.Client_PresenceUpdate;
            client.GuildBanAdded += this.Client_GuildBanAdd;
            client.GuildBanRemoved += this.Client_GuildBanRemove;
            client.GuildEmojisUpdated += this.Client_GuildEmojisUpdate;
            client.GuildIntegrationsUpdated += this.Client_GuildIntegrationsUpdate;
            client.GuildMemberAdded += this.Client_GuildMemberAdd;
            client.GuildMemberRemoved += this.Client_GuildMemberRemove;
            client.GuildMemberUpdated += this.Client_GuildMemberUpdate;
            client.GuildRoleCreated += this.Client_GuildRoleCreate;
            client.GuildRoleUpdated += this.Client_GuildRoleUpdate;
            client.GuildRoleDeleted += this.Client_GuildRoleDelete;
            client.MessageUpdated += this.Client_MessageUpdate;
            client.MessageDeleted += this.Client_MessageDelete;
            client.MessagesBulkDeleted += this.Client_MessageBulkDelete;
            client.TypingStarted += this.Client_TypingStart;
            client.UserSettingsUpdated += this.Client_UserSettingsUpdate;
            client.UserUpdated += this.Client_UserUpdate;
            client.VoiceStateUpdated += this.Client_VoiceStateUpdate;
            client.VoiceServerUpdated += this.Client_VoiceServerUpdate;
            client.GuildMembersChunked += this.Client_GuildMembersChunk;
            client.UnknownEvent += this.Client_UnknownEvent;
            client.MessageReactionAdded += this.Client_MessageReactionAdd;
            client.MessageReactionRemoved += this.Client_MessageReactionRemove;
            client.MessageReactionsCleared += this.Client_MessageReactionRemoveAll;
            client.MessageReactionRemovedEmoji += this.Client_MessageReactionRemovedEmoji;
            client.WebhooksUpdated += this.Client_WebhooksUpdate;
            client.Heartbeated += this.Client_HeartBeated;
        }

        private void UnhookEventHandlers(DiscordClient client)
        {
            client.ClientErrored -= this.Client_ClientError;
            client.SocketErrored -= this.Client_SocketError;
            client.SocketOpened -= this.Client_SocketOpened;
            client.SocketClosed -= this.Client_SocketClosed;
            client.Ready -= this.Client_Ready;
            client.Resumed -= this.Client_Resumed;
            client.ChannelCreated -= this.Client_ChannelCreated;
            client.DmChannelCreated -= this.Client_DMChannelCreated;
            client.ChannelUpdated -= this.Client_ChannelUpdated;
            client.ChannelDeleted -= this.Client_ChannelDeleted;
            client.DmChannelDeleted -= this.Client_DMChannelDeleted;
            client.ChannelPinsUpdated -= this.Client_ChannelPinsUpdated;
            client.GuildCreated -= this.Client_GuildCreated;
            client.GuildAvailable -= this.Client_GuildAvailable;
            client.GuildUpdated -= this.Client_GuildUpdated;
            client.GuildDeleted -= this.Client_GuildDeleted;
            client.GuildUnavailable -= this.Client_GuildUnavailable;
            client.GuildDownloadCompleted -= this.Client_GuildDownloadCompleted;
            client.InviteCreated -= this.Client_InviteCreated;
            client.InviteDeleted -= this.Client_InviteDeleted;
            client.MessageCreated -= this.Client_MessageCreated;
            client.PresenceUpdated -= this.Client_PresenceUpdate;
            client.GuildBanAdded -= this.Client_GuildBanAdd;
            client.GuildBanRemoved -= this.Client_GuildBanRemove;
            client.GuildEmojisUpdated -= this.Client_GuildEmojisUpdate;
            client.GuildIntegrationsUpdated -= this.Client_GuildIntegrationsUpdate;
            client.GuildMemberAdded -= this.Client_GuildMemberAdd;
            client.GuildMemberRemoved -= this.Client_GuildMemberRemove;
            client.GuildMemberUpdated -= this.Client_GuildMemberUpdate;
            client.GuildRoleCreated -= this.Client_GuildRoleCreate;
            client.GuildRoleUpdated -= this.Client_GuildRoleUpdate;
            client.GuildRoleDeleted -= this.Client_GuildRoleDelete;
            client.MessageUpdated -= this.Client_MessageUpdate;
            client.MessageDeleted -= this.Client_MessageDelete;
            client.MessagesBulkDeleted -= this.Client_MessageBulkDelete;
            client.TypingStarted -= this.Client_TypingStart;
            client.UserSettingsUpdated -= this.Client_UserSettingsUpdate;
            client.UserUpdated -= this.Client_UserUpdate;
            client.VoiceStateUpdated -= this.Client_VoiceStateUpdate;
            client.VoiceServerUpdated -= this.Client_VoiceServerUpdate;
            client.GuildMembersChunked -= this.Client_GuildMembersChunk;
            client.UnknownEvent -= this.Client_UnknownEvent;
            client.MessageReactionAdded -= this.Client_MessageReactionAdd;
            client.MessageReactionRemoved -= this.Client_MessageReactionRemove;
            client.MessageReactionsCleared -= this.Client_MessageReactionRemoveAll;
            client.MessageReactionRemovedEmoji -= this.Client_MessageReactionRemovedEmoji;
            client.WebhooksUpdated -= this.Client_WebhooksUpdate;
            client.Heartbeated -= this.Client_HeartBeated;
        }

        #endregion

        #region Destructor

        ~DiscordShardedClient()
            => this.InternalStopAsync(false).GetAwaiter().GetResult();

        #endregion
    }
}
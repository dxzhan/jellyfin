﻿#nullable enable
#pragma warning disable CA1307

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Common;
using MediaBrowser.Common.Cryptography;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Users
{
    /// <summary>
    /// Manages the creation and retrieval of <see cref="User"/> instances.
    /// </summary>
    public class UserManager : IUserManager
    {
        private readonly JellyfinDbProvider _dbProvider;
        private readonly IEventManager _eventManager;
        private readonly ICryptoProvider _cryptoProvider;
        private readonly INetworkManager _networkManager;
        private readonly IApplicationHost _appHost;
        private readonly IImageProcessor _imageProcessor;
        private readonly ILogger<UserManager> _logger;
        private readonly IReadOnlyCollection<IPasswordResetProvider> _passwordResetProviders;
        private readonly IReadOnlyCollection<IAuthenticationProvider> _authenticationProviders;
        private readonly InvalidAuthProvider _invalidAuthProvider;
        private readonly DefaultAuthenticationProvider _defaultAuthenticationProvider;
        private readonly DefaultPasswordResetProvider _defaultPasswordResetProvider;

        private readonly IDictionary<Guid, User> _users;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserManager"/> class.
        /// </summary>
        /// <param name="dbProvider">The database provider.</param>
        /// <param name="eventManager">The event manager.</param>
        /// <param name="cryptoProvider">The cryptography provider.</param>
        /// <param name="networkManager">The network manager.</param>
        /// <param name="appHost">The application host.</param>
        /// <param name="imageProcessor">The image processor.</param>
        /// <param name="logger">The logger.</param>
        public UserManager(
            JellyfinDbProvider dbProvider,
            IEventManager eventManager,
            ICryptoProvider cryptoProvider,
            INetworkManager networkManager,
            IApplicationHost appHost,
            IImageProcessor imageProcessor,
            ILogger<UserManager> logger)
        {
            _dbProvider = dbProvider;
            _eventManager = eventManager;
            _cryptoProvider = cryptoProvider;
            _networkManager = networkManager;
            _appHost = appHost;
            _imageProcessor = imageProcessor;
            _logger = logger;

            _passwordResetProviders = appHost.GetExports<IPasswordResetProvider>();
            _authenticationProviders = appHost.GetExports<IAuthenticationProvider>();

            _invalidAuthProvider = _authenticationProviders.OfType<InvalidAuthProvider>().First();
            _defaultAuthenticationProvider = _authenticationProviders.OfType<DefaultAuthenticationProvider>().First();
            _defaultPasswordResetProvider = _passwordResetProviders.OfType<DefaultPasswordResetProvider>().First();

            _users = new ConcurrentDictionary<Guid, User>();
            using var dbContext = _dbProvider.CreateContext();
            foreach (var user in dbContext.Users
                .Include(user => user.Permissions)
                .Include(user => user.Preferences)
                .Include(user => user.AccessSchedules)
                .Include(user => user.ProfileImage)
                .AsEnumerable())
            {
                _users.Add(user.Id, user);
            }
        }

        /// <inheritdoc/>
        public event EventHandler<GenericEventArgs<User>>? OnUserUpdated;

        /// <inheritdoc/>
        public IEnumerable<User> Users => _users.Values;

        /// <inheritdoc/>
        public IEnumerable<Guid> UsersIds => _users.Keys;

        /// <inheritdoc/>
        public User? GetUserById(Guid id)
        {
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Guid can't be empty", nameof(id));
            }

            _users.TryGetValue(id, out var user);
            return user;
        }

        /// <inheritdoc/>
        public User? GetUserByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Invalid username", nameof(name));
            }

            return _users.Values.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc/>
        public async Task RenameUser(User user, string newName)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new ArgumentException("Invalid username", nameof(newName));
            }

            if (user.Username.Equals(newName, StringComparison.Ordinal))
            {
                throw new ArgumentException("The new and old names must be different.");
            }

            if (Users.Any(u => u.Id != user.Id && u.Username.Equals(newName, StringComparison.Ordinal)))
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "A user with the name '{0}' already exists.",
                    newName));
            }

            user.Username = newName;
            await UpdateUserAsync(user).ConfigureAwait(false);

            OnUserUpdated?.Invoke(this, new GenericEventArgs<User>(user));
        }

        /// <inheritdoc/>
        public void UpdateUser(User user)
        {
            using var dbContext = _dbProvider.CreateContext();
            dbContext.Users.Update(user);
            dbContext.SaveChanges();
        }

        /// <inheritdoc/>
        public async Task UpdateUserAsync(User user)
        {
            await using var dbContext = _dbProvider.CreateContext();
            dbContext.Users.Update(user);

            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        internal async Task<User> CreateUserInternalAsync(string name, JellyfinDb dbContext)
        {
            // TODO: Remove after user item data is migrated.
            var max = await dbContext.Users.AsQueryable().AnyAsync().ConfigureAwait(false)
                ? await dbContext.Users.AsQueryable().Select(u => u.InternalId).MaxAsync().ConfigureAwait(false)
                : 0;

            var user = new User(
                name,
                _defaultAuthenticationProvider.GetType().FullName,
                _defaultPasswordResetProvider.GetType().FullName)
            {
                InternalId = max + 1
            };

            _users.Add(user.Id, user);

            return user;
        }

        /// <inheritdoc/>
        public async Task<User> CreateUserAsync(string name)
        {
            if (!IsValidUsername(name))
            {
                throw new ArgumentException("Usernames can contain unicode symbols, numbers (0-9), dashes (-), underscores (_), apostrophes ('), and periods (.)");
            }

            await using var dbContext = _dbProvider.CreateContext();

            var newUser = await CreateUserInternalAsync(name, dbContext).ConfigureAwait(false);

            dbContext.Users.Add(newUser);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);

            await _eventManager.PublishAsync(new UserCreatedEventArgs(newUser)).ConfigureAwait(false);

            return newUser;
        }

        /// <inheritdoc/>
        public void DeleteUser(Guid userId)
        {
            if (!_users.TryGetValue(userId, out var user))
            {
                throw new ResourceNotFoundException(nameof(userId));
            }

            if (_users.Count == 1)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "The user '{0}' cannot be deleted because there must be at least one user in the system.",
                    user.Username));
            }

            if (user.HasPermission(PermissionKind.IsAdministrator)
                && Users.Count(i => i.HasPermission(PermissionKind.IsAdministrator)) == 1)
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The user '{0}' cannot be deleted because there must be at least one admin user in the system.",
                        user.Username),
                    nameof(userId));
            }

            using var dbContext = _dbProvider.CreateContext();

            // Clear all entities related to the user from the database.
            if (user.ProfileImage != null)
            {
                dbContext.Remove(user.ProfileImage);
            }

            dbContext.RemoveRange(user.Permissions);
            dbContext.RemoveRange(user.Preferences);
            dbContext.RemoveRange(user.AccessSchedules);
            dbContext.Users.Remove(user);
            dbContext.SaveChanges();
            _users.Remove(userId);

            _eventManager.Publish(new UserDeletedEventArgs(user));
        }

        /// <inheritdoc/>
        public Task ResetPassword(User user)
        {
            return ChangePassword(user, string.Empty);
        }

        /// <inheritdoc/>
        public void ResetEasyPassword(User user)
        {
            ChangeEasyPassword(user, string.Empty, null);
        }

        /// <inheritdoc/>
        public async Task ChangePassword(User user, string newPassword)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            await GetAuthenticationProvider(user).ChangePassword(user, newPassword).ConfigureAwait(false);
            await UpdateUserAsync(user).ConfigureAwait(false);

            await _eventManager.PublishAsync(new UserPasswordChangedEventArgs(user)).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public void ChangeEasyPassword(User user, string newPassword, string? newPasswordSha1)
        {
            if (newPassword != null)
            {
                newPasswordSha1 = _cryptoProvider.CreatePasswordHash(newPassword).ToString();
            }

            if (string.IsNullOrWhiteSpace(newPasswordSha1))
            {
                throw new ArgumentNullException(nameof(newPasswordSha1));
            }

            user.EasyPassword = newPasswordSha1;
            UpdateUser(user);

            _eventManager.Publish(new UserPasswordChangedEventArgs(user));
        }

        /// <inheritdoc/>
        public UserDto GetUserDto(User user, string? remoteEndPoint = null)
        {
            var hasPassword = GetAuthenticationProvider(user).HasPassword(user);
            return new UserDto
            {
                Name = user.Username,
                Id = user.Id,
                ServerId = _appHost.SystemId,
                HasPassword = hasPassword,
                HasConfiguredPassword = hasPassword,
                HasConfiguredEasyPassword = !string.IsNullOrEmpty(user.EasyPassword),
                EnableAutoLogin = user.EnableAutoLogin,
                LastLoginDate = user.LastLoginDate,
                LastActivityDate = user.LastActivityDate,
                PrimaryImageTag = user.ProfileImage != null ? _imageProcessor.GetImageCacheTag(user) : null,
                Configuration = new UserConfiguration
                {
                    SubtitleMode = user.SubtitleMode,
                    HidePlayedInLatest = user.HidePlayedInLatest,
                    EnableLocalPassword = user.EnableLocalPassword,
                    PlayDefaultAudioTrack = user.PlayDefaultAudioTrack,
                    DisplayCollectionsView = user.DisplayCollectionsView,
                    DisplayMissingEpisodes = user.DisplayMissingEpisodes,
                    AudioLanguagePreference = user.AudioLanguagePreference,
                    RememberAudioSelections = user.RememberAudioSelections,
                    EnableNextEpisodeAutoPlay = user.EnableNextEpisodeAutoPlay,
                    RememberSubtitleSelections = user.RememberSubtitleSelections,
                    SubtitleLanguagePreference = user.SubtitleLanguagePreference ?? string.Empty,
                    OrderedViews = user.GetPreference(PreferenceKind.OrderedViews),
                    GroupedFolders = user.GetPreference(PreferenceKind.GroupedFolders),
                    MyMediaExcludes = user.GetPreference(PreferenceKind.MyMediaExcludes),
                    LatestItemsExcludes = user.GetPreference(PreferenceKind.LatestItemExcludes)
                },
                Policy = new UserPolicy
                {
                    MaxParentalRating = user.MaxParentalAgeRating,
                    EnableUserPreferenceAccess = user.EnableUserPreferenceAccess,
                    RemoteClientBitrateLimit = user.RemoteClientBitrateLimit ?? 0,
                    AuthenticationProviderId = user.AuthenticationProviderId,
                    PasswordResetProviderId = user.PasswordResetProviderId,
                    InvalidLoginAttemptCount = user.InvalidLoginAttemptCount,
                    LoginAttemptsBeforeLockout = user.LoginAttemptsBeforeLockout ?? -1,
                    MaxActiveSessions = user.MaxActiveSessions,
                    IsAdministrator = user.HasPermission(PermissionKind.IsAdministrator),
                    IsHidden = user.HasPermission(PermissionKind.IsHidden),
                    IsDisabled = user.HasPermission(PermissionKind.IsDisabled),
                    EnableSharedDeviceControl = user.HasPermission(PermissionKind.EnableSharedDeviceControl),
                    EnableRemoteAccess = user.HasPermission(PermissionKind.EnableRemoteAccess),
                    EnableLiveTvManagement = user.HasPermission(PermissionKind.EnableLiveTvManagement),
                    EnableLiveTvAccess = user.HasPermission(PermissionKind.EnableLiveTvAccess),
                    EnableMediaPlayback = user.HasPermission(PermissionKind.EnableMediaPlayback),
                    EnableAudioPlaybackTranscoding = user.HasPermission(PermissionKind.EnableAudioPlaybackTranscoding),
                    EnableVideoPlaybackTranscoding = user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding),
                    EnableContentDeletion = user.HasPermission(PermissionKind.EnableContentDeletion),
                    EnableContentDownloading = user.HasPermission(PermissionKind.EnableContentDownloading),
                    EnableSyncTranscoding = user.HasPermission(PermissionKind.EnableSyncTranscoding),
                    EnableMediaConversion = user.HasPermission(PermissionKind.EnableMediaConversion),
                    EnableAllChannels = user.HasPermission(PermissionKind.EnableAllChannels),
                    EnableAllDevices = user.HasPermission(PermissionKind.EnableAllDevices),
                    EnableAllFolders = user.HasPermission(PermissionKind.EnableAllFolders),
                    EnableRemoteControlOfOtherUsers = user.HasPermission(PermissionKind.EnableRemoteControlOfOtherUsers),
                    EnablePlaybackRemuxing = user.HasPermission(PermissionKind.EnablePlaybackRemuxing),
                    ForceRemoteSourceTranscoding = user.HasPermission(PermissionKind.ForceRemoteSourceTranscoding),
                    EnablePublicSharing = user.HasPermission(PermissionKind.EnablePublicSharing),
                    AccessSchedules = user.AccessSchedules.ToArray(),
                    BlockedTags = user.GetPreference(PreferenceKind.BlockedTags),
                    EnabledChannels = user.GetPreference(PreferenceKind.EnabledChannels)?.Select(Guid.Parse).ToArray(),
                    EnabledDevices = user.GetPreference(PreferenceKind.EnabledDevices),
                    EnabledFolders = user.GetPreference(PreferenceKind.EnabledFolders)?.Select(Guid.Parse).ToArray(),
                    EnableContentDeletionFromFolders = user.GetPreference(PreferenceKind.EnableContentDeletionFromFolders),
                    SyncPlayAccess = user.SyncPlayAccess,
                    BlockedChannels = user.GetPreference(PreferenceKind.BlockedChannels)?.Select(Guid.Parse).ToArray(),
                    BlockedMediaFolders = user.GetPreference(PreferenceKind.BlockedMediaFolders)?.Select(Guid.Parse).ToArray(),
                    BlockUnratedItems = user.GetPreference(PreferenceKind.BlockUnratedItems).Select(Enum.Parse<UnratedItem>).ToArray()
                }
            };
        }

        /// <inheritdoc/>
        public async Task<User?> AuthenticateUser(
            string username,
            string password,
            string passwordSha1,
            string remoteEndPoint,
            bool isUserSession)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                _logger.LogInformation("Authentication request without username has been denied (IP: {IP}).", remoteEndPoint);
                throw new ArgumentNullException(nameof(username));
            }

            var user = Users.FirstOrDefault(i => string.Equals(username, i.Username, StringComparison.OrdinalIgnoreCase));
            bool success;
            IAuthenticationProvider? authenticationProvider;

            if (user != null)
            {
                var authResult = await AuthenticateLocalUser(username, password, user, remoteEndPoint)
                    .ConfigureAwait(false);
                authenticationProvider = authResult.authenticationProvider;
                success = authResult.success;
            }
            else
            {
                var authResult = await AuthenticateLocalUser(username, password, null, remoteEndPoint)
                    .ConfigureAwait(false);
                authenticationProvider = authResult.authenticationProvider;
                string updatedUsername = authResult.username;
                success = authResult.success;

                if (success
                    && authenticationProvider != null
                    && !(authenticationProvider is DefaultAuthenticationProvider))
                {
                    // Trust the username returned by the authentication provider
                    username = updatedUsername;

                    // Search the database for the user again
                    // the authentication provider might have created it
                    user = Users.FirstOrDefault(i => string.Equals(username, i.Username, StringComparison.OrdinalIgnoreCase));

                    if (authenticationProvider is IHasNewUserPolicy hasNewUserPolicy && user != null)
                    {
                        UpdatePolicy(user.Id, hasNewUserPolicy.GetNewUserPolicy());

                        await UpdateUserAsync(user).ConfigureAwait(false);
                    }
                }
            }

            if (success && user != null && authenticationProvider != null)
            {
                var providerId = authenticationProvider.GetType().FullName;

                if (!string.Equals(providerId, user.AuthenticationProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    user.AuthenticationProviderId = providerId;
                    await UpdateUserAsync(user).ConfigureAwait(false);
                }
            }

            if (user == null)
            {
                _logger.LogInformation(
                    "Authentication request for {UserName} has been denied (IP: {IP}).",
                    username,
                    remoteEndPoint);
                throw new AuthenticationException("Invalid username or password entered.");
            }

            if (user.HasPermission(PermissionKind.IsDisabled))
            {
                _logger.LogInformation(
                    "Authentication request for {UserName} has been denied because this account is currently disabled (IP: {IP}).",
                    username,
                    remoteEndPoint);
                throw new SecurityException(
                    $"The {user.Username} account is currently disabled. Please consult with your administrator.");
            }

            if (!user.HasPermission(PermissionKind.EnableRemoteAccess) &&
                !_networkManager.IsInLocalNetwork(remoteEndPoint))
            {
                _logger.LogInformation(
                    "Authentication request for {UserName} forbidden: remote access disabled and user not in local network (IP: {IP}).",
                    username,
                    remoteEndPoint);
                throw new SecurityException("Forbidden.");
            }

            if (!user.IsParentalScheduleAllowed())
            {
                _logger.LogInformation(
                    "Authentication request for {UserName} is not allowed at this time due parental restrictions (IP: {IP}).",
                    username,
                    remoteEndPoint);
                throw new SecurityException("User is not allowed access at this time.");
            }

            // Update LastActivityDate and LastLoginDate, then save
            if (success)
            {
                if (isUserSession)
                {
                    user.LastActivityDate = user.LastLoginDate = DateTime.UtcNow;
                }

                user.InvalidLoginAttemptCount = 0;
                await UpdateUserAsync(user).ConfigureAwait(false);
                _logger.LogInformation("Authentication request for {UserName} has succeeded.", user.Username);
            }
            else
            {
                await IncrementInvalidLoginAttemptCount(user).ConfigureAwait(false);
                _logger.LogInformation(
                    "Authentication request for {UserName} has been denied (IP: {IP}).",
                    user.Username,
                    remoteEndPoint);
            }

            return success ? user : null;
        }

        /// <inheritdoc/>
        public async Task<ForgotPasswordResult> StartForgotPasswordProcess(string enteredUsername, bool isInNetwork)
        {
            var user = string.IsNullOrWhiteSpace(enteredUsername) ? null : GetUserByName(enteredUsername);

            if (user != null && isInNetwork)
            {
                var passwordResetProvider = GetPasswordResetProvider(user);
                var result = await passwordResetProvider
                    .StartForgotPasswordProcess(user, isInNetwork)
                    .ConfigureAwait(false);

                await UpdateUserAsync(user).ConfigureAwait(false);
                return result;
            }

            return new ForgotPasswordResult
            {
                Action = ForgotPasswordAction.InNetworkRequired,
                PinFile = string.Empty
            };
        }

        /// <inheritdoc/>
        public async Task<PinRedeemResult> RedeemPasswordResetPin(string pin)
        {
            foreach (var provider in _passwordResetProviders)
            {
                var result = await provider.RedeemPasswordResetPin(pin).ConfigureAwait(false);

                if (result.Success)
                {
                    return result;
                }
            }

            return new PinRedeemResult
            {
                Success = false,
                UsersReset = Array.Empty<string>()
            };
        }

        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            // TODO: Refactor the startup wizard so that it doesn't require a user to already exist.
            if (_users.Any())
            {
                return;
            }

            var defaultName = Environment.UserName;
            if (string.IsNullOrWhiteSpace(defaultName) || !IsValidUsername(defaultName))
            {
                defaultName = "MyJellyfinUser";
            }

            _logger.LogWarning("No users, creating one with username {UserName}", defaultName);

            await using var dbContext = _dbProvider.CreateContext();
            var newUser = await CreateUserInternalAsync(defaultName, dbContext).ConfigureAwait(false);
            newUser.SetPermission(PermissionKind.IsAdministrator, true);
            newUser.SetPermission(PermissionKind.EnableContentDeletion, true);
            newUser.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, true);

            dbContext.Users.Add(newUser);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public NameIdPair[] GetAuthenticationProviders()
        {
            return _authenticationProviders
                .Where(provider => provider.IsEnabled)
                .OrderBy(i => i is DefaultAuthenticationProvider ? 0 : 1)
                .ThenBy(i => i.Name)
                .Select(i => new NameIdPair
                {
                    Name = i.Name,
                    Id = i.GetType().FullName
                })
                .ToArray();
        }

        /// <inheritdoc/>
        public NameIdPair[] GetPasswordResetProviders()
        {
            return _passwordResetProviders
                .Where(provider => provider.IsEnabled)
                .OrderBy(i => i is DefaultPasswordResetProvider ? 0 : 1)
                .ThenBy(i => i.Name)
                .Select(i => new NameIdPair
                {
                    Name = i.Name,
                    Id = i.GetType().FullName
                })
                .ToArray();
        }

        /// <inheritdoc/>
        public void UpdateConfiguration(Guid userId, UserConfiguration config)
        {
            using var dbContext = _dbProvider.CreateContext();
            var user = dbContext.Users
                           .Include(u => u.Permissions)
                           .Include(u => u.Preferences)
                           .Include(u => u.AccessSchedules)
                           .Include(u => u.ProfileImage)
                           .FirstOrDefault(u => u.Id == userId)
                       ?? throw new ArgumentException("No user exists with given Id!");

            user.SubtitleMode = config.SubtitleMode;
            user.HidePlayedInLatest = config.HidePlayedInLatest;
            user.EnableLocalPassword = config.EnableLocalPassword;
            user.PlayDefaultAudioTrack = config.PlayDefaultAudioTrack;
            user.DisplayCollectionsView = config.DisplayCollectionsView;
            user.DisplayMissingEpisodes = config.DisplayMissingEpisodes;
            user.AudioLanguagePreference = config.AudioLanguagePreference;
            user.RememberAudioSelections = config.RememberAudioSelections;
            user.EnableNextEpisodeAutoPlay = config.EnableNextEpisodeAutoPlay;
            user.RememberSubtitleSelections = config.RememberSubtitleSelections;
            user.SubtitleLanguagePreference = config.SubtitleLanguagePreference;

            user.SetPreference(PreferenceKind.OrderedViews, config.OrderedViews);
            user.SetPreference(PreferenceKind.GroupedFolders, config.GroupedFolders);
            user.SetPreference(PreferenceKind.MyMediaExcludes, config.MyMediaExcludes);
            user.SetPreference(PreferenceKind.LatestItemExcludes, config.LatestItemsExcludes);

            dbContext.Update(user);
            dbContext.SaveChanges();
        }

        /// <inheritdoc/>
        public void UpdatePolicy(Guid userId, UserPolicy policy)
        {
            using var dbContext = _dbProvider.CreateContext();
            var user = dbContext.Users
                           .Include(u => u.Permissions)
                           .Include(u => u.Preferences)
                           .Include(u => u.AccessSchedules)
                           .Include(u => u.ProfileImage)
                           .FirstOrDefault(u => u.Id == userId)
                       ?? throw new ArgumentException("No user exists with given Id!");

            // The default number of login attempts is 3, but for some god forsaken reason it's sent to the server as "0"
            int? maxLoginAttempts = policy.LoginAttemptsBeforeLockout switch
            {
                -1 => null,
                0 => 3,
                _ => policy.LoginAttemptsBeforeLockout
            };

            user.MaxParentalAgeRating = policy.MaxParentalRating;
            user.EnableUserPreferenceAccess = policy.EnableUserPreferenceAccess;
            user.RemoteClientBitrateLimit = policy.RemoteClientBitrateLimit;
            user.AuthenticationProviderId = policy.AuthenticationProviderId;
            user.PasswordResetProviderId = policy.PasswordResetProviderId;
            user.InvalidLoginAttemptCount = policy.InvalidLoginAttemptCount;
            user.LoginAttemptsBeforeLockout = maxLoginAttempts;
            user.MaxActiveSessions = policy.MaxActiveSessions;
            user.SyncPlayAccess = policy.SyncPlayAccess;
            user.SetPermission(PermissionKind.IsAdministrator, policy.IsAdministrator);
            user.SetPermission(PermissionKind.IsHidden, policy.IsHidden);
            user.SetPermission(PermissionKind.IsDisabled, policy.IsDisabled);
            user.SetPermission(PermissionKind.EnableSharedDeviceControl, policy.EnableSharedDeviceControl);
            user.SetPermission(PermissionKind.EnableRemoteAccess, policy.EnableRemoteAccess);
            user.SetPermission(PermissionKind.EnableLiveTvManagement, policy.EnableLiveTvManagement);
            user.SetPermission(PermissionKind.EnableLiveTvAccess, policy.EnableLiveTvAccess);
            user.SetPermission(PermissionKind.EnableMediaPlayback, policy.EnableMediaPlayback);
            user.SetPermission(PermissionKind.EnableAudioPlaybackTranscoding, policy.EnableAudioPlaybackTranscoding);
            user.SetPermission(PermissionKind.EnableVideoPlaybackTranscoding, policy.EnableVideoPlaybackTranscoding);
            user.SetPermission(PermissionKind.EnableContentDeletion, policy.EnableContentDeletion);
            user.SetPermission(PermissionKind.EnableContentDownloading, policy.EnableContentDownloading);
            user.SetPermission(PermissionKind.EnableSyncTranscoding, policy.EnableSyncTranscoding);
            user.SetPermission(PermissionKind.EnableMediaConversion, policy.EnableMediaConversion);
            user.SetPermission(PermissionKind.EnableAllChannels, policy.EnableAllChannels);
            user.SetPermission(PermissionKind.EnableAllDevices, policy.EnableAllDevices);
            user.SetPermission(PermissionKind.EnableAllFolders, policy.EnableAllFolders);
            user.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, policy.EnableRemoteControlOfOtherUsers);
            user.SetPermission(PermissionKind.EnablePlaybackRemuxing, policy.EnablePlaybackRemuxing);
            user.SetPermission(PermissionKind.ForceRemoteSourceTranscoding, policy.ForceRemoteSourceTranscoding);
            user.SetPermission(PermissionKind.EnablePublicSharing, policy.EnablePublicSharing);

            user.AccessSchedules.Clear();
            foreach (var policyAccessSchedule in policy.AccessSchedules)
            {
                user.AccessSchedules.Add(policyAccessSchedule);
            }

            // TODO: fix this at some point
            user.SetPreference(
                PreferenceKind.BlockUnratedItems,
                policy.BlockUnratedItems?.Select(i => i.ToString()).ToArray() ?? Array.Empty<string>());
            user.SetPreference(PreferenceKind.BlockedTags, policy.BlockedTags);
            user.SetPreference(PreferenceKind.EnabledChannels, policy.EnabledChannels?.Select(i => i.ToString("N", CultureInfo.InvariantCulture)).ToArray());
            user.SetPreference(PreferenceKind.EnabledDevices, policy.EnabledDevices);
            user.SetPreference(PreferenceKind.EnabledFolders, policy.EnabledFolders?.Select(i => i.ToString("N", CultureInfo.InvariantCulture)).ToArray());
            user.SetPreference(PreferenceKind.EnableContentDeletionFromFolders, policy.EnableContentDeletionFromFolders);

            dbContext.Update(user);
            dbContext.SaveChanges();
        }

        /// <inheritdoc/>
        public void ClearProfileImage(User user)
        {
            using var dbContext = _dbProvider.CreateContext();
            dbContext.Remove(user.ProfileImage);
            dbContext.SaveChanges();
            user.ProfileImage = null;
        }

        private static bool IsValidUsername(string name)
        {
            // This is some regex that matches only on unicode "word" characters, as well as -, _ and @
            // In theory this will cut out most if not all 'control' characters which should help minimize any weirdness
            // Usernames can contain letters (a-z + whatever else unicode is cool with), numbers (0-9), at-signs (@), dashes (-), underscores (_), apostrophes ('), periods (.) and spaces ( )
            return Regex.IsMatch(name, @"^[\w\ \-'._@]*$");
        }

        private IAuthenticationProvider GetAuthenticationProvider(User user)
        {
            return GetAuthenticationProviders(user)[0];
        }

        private IPasswordResetProvider GetPasswordResetProvider(User user)
        {
            return GetPasswordResetProviders(user)[0];
        }

        private IList<IAuthenticationProvider> GetAuthenticationProviders(User? user)
        {
            var authenticationProviderId = user?.AuthenticationProviderId;

            var providers = _authenticationProviders.Where(i => i.IsEnabled).ToList();

            if (!string.IsNullOrEmpty(authenticationProviderId))
            {
                providers = providers.Where(i => string.Equals(authenticationProviderId, i.GetType().FullName, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (providers.Count == 0)
            {
                // Assign the user to the InvalidAuthProvider since no configured auth provider was valid/found
                _logger.LogWarning(
                    "User {Username} was found with invalid/missing Authentication Provider {AuthenticationProviderId}. Assigning user to InvalidAuthProvider until this is corrected",
                    user?.Username,
                    user?.AuthenticationProviderId);
                providers = new List<IAuthenticationProvider>
                {
                    _invalidAuthProvider
                };
            }

            return providers;
        }

        private IList<IPasswordResetProvider> GetPasswordResetProviders(User user)
        {
            var passwordResetProviderId = user.PasswordResetProviderId;
            var providers = _passwordResetProviders.Where(i => i.IsEnabled).ToArray();

            if (!string.IsNullOrEmpty(passwordResetProviderId))
            {
                providers = providers.Where(i =>
                        string.Equals(passwordResetProviderId, i.GetType().FullName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            if (providers.Length == 0)
            {
                providers = new IPasswordResetProvider[]
                {
                    _defaultPasswordResetProvider
                };
            }

            return providers;
        }

        private async Task<(IAuthenticationProvider? authenticationProvider, string username, bool success)> AuthenticateLocalUser(
                string username,
                string password,
                User? user,
                string remoteEndPoint)
        {
            bool success = false;
            IAuthenticationProvider? authenticationProvider = null;

            foreach (var provider in GetAuthenticationProviders(user))
            {
                var providerAuthResult =
                    await AuthenticateWithProvider(provider, username, password, user).ConfigureAwait(false);
                var updatedUsername = providerAuthResult.username;
                success = providerAuthResult.success;

                if (success)
                {
                    authenticationProvider = provider;
                    username = updatedUsername;
                    break;
                }
            }

            if (!success
                && _networkManager.IsInLocalNetwork(remoteEndPoint)
                && user?.EnableLocalPassword == true
                && !string.IsNullOrEmpty(user.EasyPassword))
            {
                // Check easy password
                var passwordHash = PasswordHash.Parse(user.EasyPassword);
                var hash = _cryptoProvider.ComputeHash(
                    passwordHash.Id,
                    Encoding.UTF8.GetBytes(password),
                    passwordHash.Salt.ToArray());
                success = passwordHash.Hash.SequenceEqual(hash);
            }

            return (authenticationProvider, username, success);
        }

        private async Task<(string username, bool success)> AuthenticateWithProvider(
            IAuthenticationProvider provider,
            string username,
            string password,
            User? resolvedUser)
        {
            try
            {
                var authenticationResult = provider is IRequiresResolvedUser requiresResolvedUser
                    ? await requiresResolvedUser.Authenticate(username, password, resolvedUser).ConfigureAwait(false)
                    : await provider.Authenticate(username, password).ConfigureAwait(false);

                if (authenticationResult.Username != username)
                {
                    _logger.LogDebug("Authentication provider provided updated username {1}", authenticationResult.Username);
                    username = authenticationResult.Username;
                }

                return (username, true);
            }
            catch (AuthenticationException ex)
            {
                _logger.LogError(ex, "Error authenticating with provider {Provider}", provider.Name);

                return (username, false);
            }
        }

        private async Task IncrementInvalidLoginAttemptCount(User user)
        {
            user.InvalidLoginAttemptCount++;
            int? maxInvalidLogins = user.LoginAttemptsBeforeLockout;
            if (maxInvalidLogins.HasValue && user.InvalidLoginAttemptCount >= maxInvalidLogins)
            {
                user.SetPermission(PermissionKind.IsDisabled, true);
                await _eventManager.PublishAsync(new UserLockedOutEventArgs(user)).ConfigureAwait(false);
                _logger.LogWarning(
                    "Disabling user {Username} due to {Attempts} unsuccessful login attempts.",
                    user.Username,
                    user.InvalidLoginAttemptCount);
            }

            await UpdateUserAsync(user).ConfigureAwait(false);
        }
    }
}

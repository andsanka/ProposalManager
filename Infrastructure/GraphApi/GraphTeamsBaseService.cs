﻿// Copyright(c) Microsoft Corporation. 
// All rights reserved.
//
// Licensed under the MIT license. See LICENSE file in the solution root folder for full license information.

using ApplicationCore;
using ApplicationCore.Helpers;
using ApplicationCore.Interfaces;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Infrastructure.GraphApi
{
    public abstract class GraphTeamsBaseService : BaseService<GraphTeamsBaseService>
    {
        protected readonly IGraphClientContext _graphClientContext;
        protected readonly IUserContext _userContext;
        protected readonly IAzureKeyVaultService _azureKeyVaultService;

        public GraphTeamsBaseService(
            ILogger<GraphTeamsBaseService> logger,
            IOptionsMonitor<AppOptions> appOptions,
            IGraphClientContext graphClientContext,
            IUserContext userContext,
            IAzureKeyVaultService azureKeyVaultService) : base(logger, appOptions)
        {
            Guard.Against.Null(graphClientContext, nameof(graphClientContext));
            _graphClientContext = graphClientContext;
            _userContext = userContext;
            _azureKeyVaultService = azureKeyVaultService;
        }

        /// <summary>
        /// Graph Service client
        /// </summary>
        public GraphServiceClient GraphClient => _graphClientContext?.GraphClient;

        public async Task<JObject> CreateGroupAsync(string displayName, string description = "")
        {
            // POST: https://graph.microsoft.com/v1.0/groups
            // EXAMPLE: https://graph.microsoft.com/v1.0/groups

            _logger.LogInformation("CreateGroupAsync called.");
            try
            {
                Guard.Against.Null(displayName, nameof(displayName));

                // Create JSON object with group settings
                var groupTypesObject = new List<string> { "Unified" };

                //get owner
                string objectId = _userContext.User.FindFirst(AzureAdConstants.ObjectIdClaimType).Value;
                var userId = $"https://graph.microsoft.com/v1.0/Users/{objectId}";

                Regex rx = new Regex(@"[^a-zA-Z0-9-.\/s]");
                string mailNickname = rx.Replace(displayName, "");

                var groupSettings = new GroupSettings()
                {
                    Description = description,
                    DisplayName = displayName,
                    GroupTypes = new[] { "Unified" },
                    MailEnabled = true,
                    MailNickname = mailNickname,
                    Members = new[] { userId },
                    Owners = new[] { userId },
                    SecurityEnabled = false
                };

                var payload = JsonConvert.SerializeObject(groupSettings);
                
                var requestUrl = _appOptions.GraphRequestUrl + "groups";
                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 201.
                if (response.StatusCode != System.Net.HttpStatusCode.Created)
                {
                    _logger.LogError("CreateGroupAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("CreateGroupAsync end.");
                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("CreateGroupAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> ListGroupsAsync(string filter = "", string select = "id")
        {
            // GET: https://graph.microsoft.com/v1.0/groups
            // EXAMPLE: https://graph.microsoft.com/v1.0/groups?$select=id
            // EXAMPLE: https://graph.microsoft.com/v1.0/groups?$filter=startswith(displayName, 'XZZ company')&$select=id

            _logger.LogInformation("ListGroupsAsync called.");
            try
            {
                var requestUrl = _appOptions.GraphRequestUrl + "/groups";

                var concat = "?$";
                if (!String.IsNullOrEmpty(filter))
                {
                    requestUrl = requestUrl + concat + "filter=" + filter;
                    concat = "&$";
                }
                if (!String.IsNullOrEmpty(select))
                {
                    requestUrl = requestUrl + concat + "select=" + select;
                }

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Get, requestUrl);

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 201.
                if (response.StatusCode != System.Net.HttpStatusCode.Created)
                {
                    _logger.LogError("ListGroupsAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("ListGroupsAsync end.");
                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("ListGroupsAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> CreateTeamAsync(string displayName, string description = "")
        {
            // 2 step process: create group then create team using the if from create group
            // PUT: https://graph.microsoft.com/beta/groups/{group-id-for-teams}/team
            // EXAMPLE: https://graph.microsoft.com/beta/groups/ac738d44-8541-4fe5-9b01-f80202a5a908/team

            _logger.LogInformation("CreateTeamAsync called.");
            try
            {
                Guard.Against.Null(displayName, nameof(displayName));

                var createGroup = await CreateGroupAsync(displayName, description);
                var groupId = createGroup["id"].ToString();

                // Create JSON object with team settings
                dynamic memberSettingsObject = new JObject();
                memberSettingsObject.allowCreateUpdateChannels = true;

                dynamic messagingSettingsObject = new JObject();
                messagingSettingsObject.allowUserEditMessages = true;
                messagingSettingsObject.allowUserDeleteMessages = true;

                dynamic funSettingsObject = new JObject();
                funSettingsObject.allowGiphy = true;
                funSettingsObject.giphyContentRating = "strict";

                dynamic teamSettingsObject = new JObject();
                teamSettingsObject.memberSettings = memberSettingsObject;
                teamSettingsObject.messagingSettings = messagingSettingsObject;
                teamSettingsObject.funSettings = funSettingsObject;

                var requestUrl = _appOptions.GraphRequestUrl + "groups/" + groupId + "/team";

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Put, requestUrl)
                {
                    Content = new StringContent(teamSettingsObject.ToString(), Encoding.UTF8, "application/json")
                };

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 201.
                if (response.StatusCode != System.Net.HttpStatusCode.Created)
                {
                    _logger.LogError("CreateTeamAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {                
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);
                    _logger.LogInformation("CreateTeamAsync end.");
                    return responseJObject;
                }
               
            }
            catch (ServiceException ex)
            {
                _logger.LogError("CreateTeamAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> GetTeamAsync(string groupId)
        {
            // GET: https://graph.microsoft.com/v1.0/teams/{group-id-for-teams}

            _logger.LogInformation("GetTeamAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));

                var requestUrl = _appOptions.GraphRequestUrl + "teams/" + groupId;

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Get, requestUrl);

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 200.
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    _logger.LogError("GetTeamAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("GetTeamAsync end.");
                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("GetTeamAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> UpdateTeamAsync(string groupId)
        {
            // PATCH: https://graph.microsoft.com/v1.0/teams/{group-id-for-teams}

            _logger.LogInformation("UpdateTeamAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));

                // Create JSON object with team settings
                dynamic memberSettingsObject = new JObject();
                memberSettingsObject.allowCreateUpdateChannels = true;

                dynamic messagingSettingsObject = new JObject();
                messagingSettingsObject.allowUserEditMessages = true;
                messagingSettingsObject.allowUserDeleteMessages = true;

                dynamic funSettingsObject = new JObject();
                funSettingsObject.allowGiphy = true;
                funSettingsObject.giphyContentRating = "strict";

                dynamic teamSettingsObject = new JObject();
                teamSettingsObject.memberSettings = memberSettingsObject;
                teamSettingsObject.messagingSettings = messagingSettingsObject;
                teamSettingsObject.funSettings = funSettingsObject;

                var requestUrl = _appOptions.GraphRequestUrl + "teams/" + groupId;
                var method = new HttpMethod("PATCH");

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(method, requestUrl);
                hrm.Content = new StringContent(teamSettingsObject.ToString(), Encoding.UTF8, "application/json");

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 204.
                if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
                {
                    _logger.LogError("UpdateTeamAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("UpdateTeamAsync end.");
                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("UpdateTeamAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> CreateChannelAsync(string groupId, string displayName, string description = "")
        {
            // POST: https://graph.microsoft.com/1.0/teams/{group-id-for-teams}/channels

            _logger.LogInformation("CreateChannelAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));
                Guard.Against.Null(displayName, nameof(displayName));

                // Create JSON object to with team settings
                dynamic channelSettingsObject = new JObject();
                channelSettingsObject.displayName = displayName;
                channelSettingsObject.description = description;

                var requestUrl = _appOptions.GraphRequestUrl + "teams/" + groupId + "/channels";

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Post, requestUrl) { Content = new StringContent(channelSettingsObject.ToString(), Encoding.UTF8, "application/json") };

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 201.
                if (response.StatusCode != System.Net.HttpStatusCode.Created)
                {
                    _logger.LogError("CreateChannelAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("CreateChannelAsync end.");

                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("CreateChannelAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<string> GetAppIdAsync(string groupId)
        {
            // GET: https://graph.microsoft.com/v1.0/teams/{group-id-for-teams}/installedApps?$expand=teamsAppDefinition

            _logger.LogInformation("GetJObjectAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));

                var requestUrl = _appOptions.GraphRequestUrl + "teams/" + groupId + "/installedApps?$expand=teamsAppDefinition";

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Get, requestUrl);

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 201.
                if (response.StatusCode != System.Net.HttpStatusCode.Created)
                {
                    _logger.LogError("GetJObjectAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("GetJObjectAsync end.");
                    return responseJObject.ToString();
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("GetJObjectAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> GetChannelAsync(string groupId, string channelId)
        {
            // GET: https://graph.microsoft.com/1,0/teams/{group-id-for-teams}/channels/{channel-id-for-teams}

            _logger.LogInformation("GetChannelAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));
                Guard.Against.Null(channelId, nameof(channelId));

                var requestUrl = _appOptions.GraphRequestUrl + "teams/" + groupId + "/channels/" + channelId;

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Get, requestUrl);

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 200.
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    _logger.LogError("GetChannelAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("GetChannelAsync end.");
                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("GetChannelAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> ListChannelAsync(string groupId)
        {
            // GET: https://graph.microsoft.com/1.0/teams/{group-id-for-teams}/channels

            _logger.LogInformation("ListChannelAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));

                var requestUrl = _appOptions.GraphRequestUrl + "teams/" + groupId + "/channels";

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Get, requestUrl);

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 200.
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    _logger.LogError("ListChannelAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("ListChannelAsync end.");
                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("ListChannelAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> AddMemberAsync(string groupId, string memberId)
        {
            // POST https://graph.microsoft.com/v1.0/groups/{id}/members/$ref
            // EXAMPLE: https://graph.microsoft.com/v1.0/groups/69a940ef-b226-4ef2-9657-d27fab2f7cf9/members/$ref

            _logger.LogInformation("AddMemberAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));
                Guard.Against.Null(memberId, nameof(memberId));

                // Create JSON object to with team settings
                var memberSettingsObject = "{ '@odata.id': 'https://graph.microsoft.com/beta/directoryObjects/" + memberId + "' }";

                var requestUrl = _appOptions.GraphRequestUrl + "groups/" + groupId + "/members/$ref";

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Content = new StringContent(memberSettingsObject.ToString(), Encoding.UTF8, "application/json")
                };

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 204.
                if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
                {
                    _logger.LogError("AddMemberAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("AddMemberAsync end.");
                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("AddMemberAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> AddMemberByUpnAsync(string groupId, string userPrincipalName)
        {
            _logger.LogInformation("AddMemberByUpnAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));
                Guard.Against.Null(userPrincipalName, nameof(userPrincipalName));

                var getUser = await GetUserAsync(userPrincipalName);
                var memberId = getUser["id"].ToString();

                _logger.LogInformation("AddMemberByUpnAsync end.");
                return await AddMemberAsync(groupId, memberId);
            }
            catch (Exception ex)
            {
                _logger.LogError("AddMemberByUpnAsync Exception: " + ex.Message);
                throw;
            }
        }

        public async Task<JObject> RemoveMemberAsync(string groupId, string memberId)
        {
            // DELETE https://graph.microsoft.com/v1.0/groups/{id}/members/{id}/$ref

            _logger.LogInformation("RemoveMemberAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));
                Guard.Against.Null(memberId, nameof(memberId));

                var requestUrl = _appOptions.GraphRequestUrl + "groups/" + groupId + "/members/" + memberId + "$ref";

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Delete, requestUrl);

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 204.
                if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
                {
                    _logger.LogError("RemoveMemberAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    JObject responseJObject = JObject.Parse(response.StatusCode.ToString());
                    _logger.LogInformation("RemoveMemberAsync end.");
                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("RemoveMemberAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> RemoveMemberByUpnAsync(string groupId, string userPrincipalName)
        {
            _logger.LogInformation("RemoveMemberByUpnAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));
                Guard.Against.Null(userPrincipalName, nameof(userPrincipalName));

                var getUser = await GetUserAsync(userPrincipalName);
                var memberId = getUser["id"].ToString();

                _logger.LogInformation("RemoveMemberByUpnAsync end.");
                return await RemoveMemberAsync(groupId, memberId);
            }
            catch (Exception ex)
            {
                _logger.LogError("RemoveMemberByUpnAsync Exception: " + ex.Message);
                throw;
            }
        }

        public async Task<JObject> GetUserAsync(string userPrincipalName)
        {
            // GET: https://graph.microsoft.com/beta/users/{userPrincipalName}
            // EXAMPLE: https://graph.microsoft.com/beta/users/admin@onterawe.onmicrosoft.com

            _logger.LogInformation("GetUserAsync called.");
            try
            {
                Guard.Against.Null(userPrincipalName, nameof(userPrincipalName));

                var channelId = Guid.NewGuid().ToString();
                var requestUrl = _appOptions.GraphBetaRequestUrl + "/users/" + userPrincipalName;

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Get, requestUrl);

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 200.
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    _logger.LogError("GetUserAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("GetUserAsync end.");
                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("GetUserAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        public async Task<JObject> GetChannelDriveAsync(string groupId)
        {
            // GET: https://graph.microsoft.com/beta/sites/{group-id-for-teams}/drive
            // EXAMPLE: https://graph.microsoft.com/beta/groups/4c60d18d-d796-4b51-976c-ea67c6ceb9c2/team


            // TODO: Finish implementation
            _logger.LogInformation("GetChannelDriveAsync called.");
            try
            {
                Guard.Against.Null(groupId, nameof(groupId));

                var requestUrl = _appOptions.GraphBetaRequestUrl + "/groups/" + groupId + "/team";

                // Create the request message and add the content.
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Get, requestUrl);

                // Authenticate (add access token) our HttpRequestMessage
                await GraphClient.AuthenticationProvider.AuthenticateRequestAsync(hrm);

                // Send the request and get the response.
                var response = await GraphClient.HttpProvider.SendAsync(hrm);

                // Get the status response and throw if is not 200.
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    _logger.LogError("GetTeamAsync error status code: " + response.StatusCode);
                    throw new ServiceException(new Error { Code = ErrorConstants.Codes.InvalidRequest, Message = response.StatusCode.ToString() });
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    JObject responseJObject = JObject.Parse(content);

                    _logger.LogInformation("GetTeamAsync end.");
                    return responseJObject;
                }
            }
            catch (ServiceException ex)
            {
                _logger.LogError("GetTeamAsync Service Exception: " + ex.Message);
                switch (ex.Error.Code)
                {
                    case "Request_ResourceNotFound":
                    case "ResourceNotFound":
                    case "ErrorItemNotFound":
                    case "itemNotFound":
                        throw;
                    case "TokenNotFound":
                        //await HttpContext.ChallengeAsync();
                        throw;
                    default:
                        throw;
                }
            }
        }

        private class GroupSettings
        {
            [JsonProperty(PropertyName = "description")]
            public string Description { get; set; }
            [JsonProperty(PropertyName = "displayName")]
            public string DisplayName { get; set; }
            [JsonProperty(PropertyName = "groupTypes")]
            public string[] GroupTypes { get; set; }
            [JsonProperty(PropertyName = "mailEnabled")]
            public bool MailEnabled { get; set; }
            [JsonProperty(PropertyName = "mailNickname")]
            public string MailNickname { get; set; }
            [JsonProperty(PropertyName = "securityEnabled")]
            public bool SecurityEnabled { get; set; }
            [JsonProperty(PropertyName = "owners@odata.bind")]
            public string[] Owners { get; set; }
            [JsonProperty(PropertyName = "members@odata.bind")]
            public string[] Members { get; set; }
        }
    }
}

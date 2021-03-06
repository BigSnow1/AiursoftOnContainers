﻿using Aiursoft.Pylon;
using Aiursoft.Pylon.Attributes;
using Aiursoft.Pylon.Models;
using Aiursoft.Pylon.Models.ForApps.AddressModels;
using Aiursoft.Pylon.Models.Stargate.ListenAddressModels;
using Aiursoft.Pylon.Services;
using Aiursoft.Pylon.Services.ToStargateServer;
using Kahla.Server.Data;
using Kahla.Server.Models;
using Kahla.Server.Models.ApiAddressModels;
using Kahla.Server.Models.ApiViewModels;
using Kahla.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aiursoft.Pylon.Services.ToAPIServer;
using Kahla.Server.Attributes;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace Kahla.Server.Controllers
{
    [APIExpHandler]
    [APIModelStateChecker]
    public class ApiController : Controller
    {
        private readonly UserManager<KahlaUser> _userManager;
        private readonly SignInManager<KahlaUser> _signInManager;
        private readonly KahlaDbContext _dbContext;
        private readonly PushKahlaMessageService _pusher;
        private readonly IConfiguration _configuration;
        private readonly AuthService<KahlaUser> _authService;
        private readonly ServiceLocation _serviceLocation;
        private readonly OAuthService _oauthService;
        private readonly ChannelService _channelService;
        private readonly StorageService _storageService;
        private readonly AppsContainer _appsContainer;
        private readonly UserService _userService;

        public ApiController(
            UserManager<KahlaUser> userManager,
            SignInManager<KahlaUser> signInManager,
            KahlaDbContext dbContext,
            PushKahlaMessageService pushService,
            IConfiguration configuration,
            AuthService<KahlaUser> authService,
            ServiceLocation serviceLocation,
            OAuthService oauthService,
            ChannelService channelService,
            StorageService storageService,
            AppsContainer appsContainer,
            UserService userService)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _dbContext = dbContext;
            _pusher = pushService;
            _configuration = configuration;
            _authService = authService;
            _serviceLocation = serviceLocation;
            _oauthService = oauthService;
            _channelService = channelService;
            _storageService = storageService;
            _appsContainer = appsContainer;
            _userService = userService;
        }

        public IActionResult Version()
        {
            return Json(new VersionViewModel
            {
                LatestVersion = _configuration["AppVersion"],
                OldestSupportedVersion = _configuration["AppVersion"],
                Message = "Successfully get the lastest version number for Kahla.",
                DownloadAddress = _serviceLocation.KahlaHome
            });
        }

        [HttpPost]
        public async Task<IActionResult> AuthByPassword(AuthByPasswordAddressModel model)
        {
            var pack = await _oauthService.PasswordAuthAsync(Extends.CurrentAppId, model.Email, model.Password);
            if (pack.Code != ErrorType.Success)
            {
                return this.Protocal(ErrorType.Unauthorized, pack.Message);
            }
            var user = await _authService.AuthApp(new AuthResultAddressModel
            {
                code = pack.Value,
                state = string.Empty
            }, isPersistent: true);
            return Json(new AiurProtocal()
            {
                Code = ErrorType.Success,
                Message = "Auth success."
            });
        }

        [HttpPost]
        [KahlaRequireCredential]
        [FileChecker]
        public async Task<IActionResult> UploadFile()
        {
            string iconPath = string.Empty;
            var file = Request.Form.Files.First();
            iconPath = await _storageService.SaveToOSS(file, Convert.ToInt32(_configuration["KahlaBucketId"]), 7, SaveFileOptions.RandomName);
            return Json(new AiurValue<string>(iconPath)
            {
                Code = ErrorType.Success,
                Message = "Successfully uploaded your file!"
            });
        }

        [HttpPost]
        public async Task<IActionResult> RegisterKahla(RegisterKahlaAddressModel model)
        {
            var result = await _oauthService.AppRegisterAsync(model.Email, model.Password, model.ConfirmPassword);
            return Json(result);
        }

        public async Task<IActionResult> SignInStatus()
        {
            var user = await GetKahlaUser();
            var signedIn = user != null;
            return Json(new AiurValue<bool>(signedIn)
            {
                Code = ErrorType.Success,
                Message = "Successfully get your signin status."
            });
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> Me()
        {
            var user = await GetKahlaUser();
            return Json(new AiurValue<KahlaUser>(user)
            {
                Code = ErrorType.Success,
                Message = "Successfully get your information."
            });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateInfo(UpdateInfoAddressModel model)
        {
            var cuser = await GetKahlaUser();
            if (!string.IsNullOrEmpty(model.NickName))
            {
                cuser.NickName = model.NickName;
            }
            if (!string.IsNullOrEmpty(model.HeadImgUrl))
            {
                cuser.HeadImgUrl = model.HeadImgUrl;
            }
            cuser.Bio = model.Bio;
            await _userService.ChangeProfileAsync(cuser.Id, await _appsContainer.AccessToken(), cuser.NickName, cuser.HeadImgUrl, cuser.Bio);
            await _userManager.UpdateAsync(cuser);
            return this.Protocal(ErrorType.Success, "Successfully set your personal info.");
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> MyFriends([Required]bool? orderByName)
        {
            var user = await GetKahlaUser();
            var list = new List<ContactInfo>();
            var conversations = await _dbContext.MyConversations(user.Id);
            foreach (var conversation in conversations)
            {
                list.Add(new ContactInfo
                {
                    ConversationId = conversation.Id,
                    DisplayName = conversation.GetDisplayName(user.Id),
                    DisplayImage = conversation.GetDisplayImage(user.Id),
                    LatestMessage = conversation.GetLatestMessage().Content,
                    LatestMessageTime = conversation.GetLatestMessage().SendTime,
                    UnReadAmount = conversation.GetUnReadAmount(user.Id),
                    Discriminator = conversation.Discriminator,
                    UserId = conversation is PrivateConversation ? (conversation as PrivateConversation).AnotherUser(user.Id).Id : null
                });
            }
            list = orderByName == true ?
                list.OrderBy(t => t.DisplayName).ToList() :
                list.OrderByDescending(t => t.LatestMessageTime).ToList();
            return Json(new AiurCollection<ContactInfo>(list)
            {
                Code = ErrorType.Success,
                Message = "Successfully get all your friends."
            });
        }

        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> DeleteFriend([Required]string id)
        {
            var user = await GetKahlaUser();
            var target = await _dbContext.Users.FindAsync(id);
            if (target == null)
                return this.Protocal(ErrorType.NotFound, "We can not find target user.");
            if (!await _dbContext.AreFriends(user.Id, target.Id))
                return this.Protocal(ErrorType.NotEnoughResources, "He is not your friend at all.");
            await _dbContext.RemoveFriend(user.Id, target.Id);
            await _dbContext.SaveChangesAsync();
            await _pusher.WereDeletedEvent(target.Id);
            return this.Protocal(ErrorType.Success, "Successfully deleted your friend relationship.");
        }

        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> CreateRequest([Required]string id)
        {
            var user = await GetKahlaUser();
            var target = await _dbContext.Users.FindAsync(id);
            if (target == null)
                return this.Protocal(ErrorType.NotFound, "We can not find your target user!");
            if (target.Id == user.Id)
                return this.Protocal(ErrorType.RequireAttention, "You can't request yourself!");
            var pending = _dbContext.Requests
                .Where(t => t.CreatorId == user.Id)
                .Where(t => t.TargetId == id)
                .Exists(t => !t.Completed);
            if (pending)
                return this.Protocal(ErrorType.HasDoneAlready, "There are some pending request hasn't been completed!");
            var areFriends = await _dbContext.AreFriends(user.Id, target.Id);
            if (areFriends)
                return this.Protocal(ErrorType.HasDoneAlready, "You two are already friends!");
            var request = new Request { CreatorId = user.Id, TargetId = id };
            _dbContext.Requests.Add(request);
            await _dbContext.SaveChangesAsync();
            await _pusher.NewFriendRequestEvent(target.Id, user.Id);
            return Json(new AiurValue<int>(request.Id)
            {
                Code = ErrorType.Success,
                Message = "Successfully created your request!"
            });
        }

        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> CompleteRequest(CompleteRequestAddressModel model)
        {
            var user = await GetKahlaUser();
            var request = await _dbContext.Requests.FindAsync(model.Id);
            if (request == null)
                return this.Protocal(ErrorType.NotFound, "We can not find target request.");
            if (request.TargetId != user.Id)
                return this.Protocal(ErrorType.Unauthorized, "The target user of this request is not you.");
            if (request.Completed == true)
                return this.Protocal(ErrorType.HasDoneAlready, "The target request is already completed.");
            request.Completed = true;
            if (model.Accept)
            {
                if (await _dbContext.AreFriends(request.CreatorId, request.TargetId))
                {
                    await _dbContext.SaveChangesAsync();
                    return this.Protocal(ErrorType.RequireAttention, "You two are already friends.");
                }
                _dbContext.AddFriend(request.CreatorId, request.TargetId);
                await _pusher.FriendAcceptedEvent(request.CreatorId);
            }
            await _dbContext.SaveChangesAsync();
            return this.Protocal(ErrorType.Success, "You have successfully completed this request.");
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> MyRequests()
        {
            var user = await GetKahlaUser();
            var requests = await _dbContext
                .Requests
                .AsNoTracking()
                .Include(t => t.Creator)
                .Where(t => t.TargetId == user.Id)
                .OrderByDescending(t => t.CreateTime)
                .ToListAsync();
            return Json(new AiurCollection<Request>(requests)
            {
                Code = ErrorType.Success,
                Message = "Successfully get your requests list."
            });
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> SearchFriends(SearchFriendsAddressModel model)
        {
            var users = await _dbContext
                .Users
                .AsNoTracking()
                .Where(t => t.NickName.ToLower().Contains(model.NickName.ToLower()))
                .ToListAsync();

            return Json(new AiurCollection<KahlaUser>(users)
            {
                Code = ErrorType.Success,
                Message = "Search result is shown."
            });
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> GetMessage([Required]int id, int take = 15)
        {
            var user = await GetKahlaUser();
            var target = await _dbContext.Conversations.FindAsync(id);
            if (!await _dbContext.VerifyJoined(user.Id, target))
                return this.Protocal(ErrorType.Unauthorized, "You don't have any relationship with that conversation.");
            //Get Messages
            var allMessages = await _dbContext
                .Messages
                .AsNoTracking()
                .Where(t => t.ConversationId == target.Id)
                .Include(t => t.Sender)
                .OrderByDescending(t => t.SendTime)
                .Take(take)
                .OrderBy(t => t.SendTime)
                .ToListAsync();
            if (target.Discriminator == nameof(PrivateConversation))
            {
                await _dbContext.Messages
                    .Where(t => t.ConversationId == target.Id)
                    .Where(t => t.SenderId != user.Id)
                    .Where(t => t.Read == false)
                    .ForEachAsync(t => t.Read = true);
            }
            else if (target.Discriminator == nameof(GroupConversation))
            {
                var relation = await _dbContext.UserGroupRelations
                    .SingleOrDefaultAsync(t => t.UserId == user.Id && t.GroupId == target.Id);
                relation.ReadTimeStamp = DateTime.Now;
            }
            await _dbContext.SaveChangesAsync();
            return Json(new AiurCollection<Message>(allMessages)
            {
                Code = ErrorType.Success,
                Message = "Successfully get all your messages."
            });
        }

        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> SendMessage(SendMessageAddressModel model)
        {
            var user = await GetKahlaUser();
            var target = await _dbContext.Conversations.FindAsync(model.Id);
            if (!await _dbContext.VerifyJoined(user.Id, target))
                return this.Protocal(ErrorType.Unauthorized, "You don't have any relationship with that conversation.");
            if (model.Content.Trim().Length == 0)
                return this.Protocal(ErrorType.InvalidInput, "Can not send empty message.");
            //Create message.
            var message = new Message
            {
                Content = model.Content,
                SenderId = user.Id,
                ConversationId = target.Id
            };
            _dbContext.Messages.Add(message);
            await _dbContext.SaveChangesAsync();
            //Push the message to reciever
            if (target.Discriminator == nameof(PrivateConversation))
            {
                var privateConversation = target as PrivateConversation;
                await _pusher.NewMessageEvent(privateConversation.RequesterId, target.Id, model.Content, user);
                await _pusher.NewMessageEvent(privateConversation.TargetId, target.Id, model.Content, user);
            }
            else if (target.Discriminator == nameof(GroupConversation))
            {
                var usersJoined = _dbContext.UserGroupRelations.Where(t => t.GroupId == target.Id);
                await usersJoined.ForEachAsync(async t => await _pusher.NewMessageEvent(t.UserId, target.Id, model.Content, user));
            }
            //Return success message.
            return this.Protocal(ErrorType.Success, "Your message has been sent.");
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> UserDetail([Required]string id)
        {
            var user = await GetKahlaUser();
            var target = await _dbContext.Users.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id);
            var model = new UserDetailViewModel();
            if (target == null)
            {
                model.Message = "We can not find target user.";
                model.Code = ErrorType.NotFound;
                return Json(model);
            }
            var conversation = await _dbContext.FindConversation(user.Id, target.Id);
            if (conversation != null)
            {
                model.User = target;
                model.AreFriends = true;
                model.ConversationId = conversation.Id;
                model.Message = "Found that user.";
                model.Code = ErrorType.Success;
            }
            else
            {
                model.User = target;
                model.AreFriends = false;
                model.ConversationId = 0;
                model.Message = "Found that user.";
                model.Code = ErrorType.Success;
            }
            return Json(model);
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> ConversationDetail([Required]int id)
        {
            var user = await GetKahlaUser();
            var conversations = await _dbContext.MyConversations(user.Id);
            var target = conversations.SingleOrDefault(t => t.Id == id);
            target.DisplayName = target.GetDisplayName(user.Id);
            target.DisplayImage = target.GetDisplayImage(user.Id);
            if (target is PrivateConversation)
            {
                var pTarget = target as PrivateConversation;
                pTarget.AnotherUserId = pTarget.AnotherUser(user.Id).Id;
                return Json(new AiurValue<PrivateConversation>(pTarget)
                {
                    Code = ErrorType.Success,
                    Message = "Successfully get target conversation."
                });
            }
            else if (target is GroupConversation)
            {
                var gtarget = target as GroupConversation;
                return Json(new AiurValue<GroupConversation>(gtarget)
                {
                    Code = ErrorType.Success,
                    Message = "Successfully get target conversation."
                });
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> CreateGroupConversation(CreateGroupConversationAddressModel model)
        {
            var user = await GetKahlaUser();
            var exsists = _dbContext.GroupConversations.Exists(t => t.GroupName == model.GroupName);
            if (exsists)
            {
                return this.Protocal(ErrorType.NotEnoughResources, $"A group with name: {model.GroupName} was already exists!");
            }
            var createdGroup = await _dbContext.CreateGroup(model.GroupName);
            var newRelationship = new UserGroupRelation
            {
                UserId = user.Id,
                GroupId = createdGroup.Id,
                ReadTimeStamp = DateTime.MinValue
            };
            _dbContext.UserGroupRelations.Add(newRelationship);
            await _dbContext.SaveChangesAsync();
            return Json(new AiurValue<int>(createdGroup.Id)
            {
                Code = ErrorType.Success,
                Message = "You have successfully created a new group and joined it!"
            });
        }

        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> JoinGroup([Required]string groupName)
        {
            var user = await GetKahlaUser();
            var group = await _dbContext.GroupConversations.SingleOrDefaultAsync(t => t.GroupName == groupName);
            if (group == null)
            {
                return this.Protocal(ErrorType.NotFound, $"We can not find a group with name: {groupName}!");
            }
            var joined = _dbContext.UserGroupRelations.Exists(t => t.UserId == user.Id && t.GroupId == group.Id);
            if (joined)
            {
                return this.Protocal(ErrorType.HasDoneAlready, $"You have already joined the group: {groupName}!");
            }
            // All checked and able to join him.
            // Warning: Currently we do not have invitation system for invitation control is too complicated.
            var newRelationship = new UserGroupRelation
            {
                UserId = user.Id,
                GroupId = group.Id
            };
            _dbContext.UserGroupRelations.Add(newRelationship);
            await _dbContext.SaveChangesAsync();
            return this.Protocal(ErrorType.Success, $"You have successfully joint the group: {groupName}!");
        }

        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> LeaveGroup(string groupName)
        {
            var user = await GetKahlaUser();
            var group = await _dbContext.GroupConversations.SingleOrDefaultAsync(t => t.GroupName == groupName);
            if (group == null)
            {
                return this.Protocal(ErrorType.NotFound, $"We can not find a group with name: {groupName}!");
            }
            var joined = await _dbContext.UserGroupRelations.SingleOrDefaultAsync(t => t.UserId == user.Id && t.GroupId == group.Id);
            if (joined != null)
            {
                return this.Protocal(ErrorType.HasDoneAlready, $"You did not joined the group: {groupName} at all!");
            }
            _dbContext.UserGroupRelations.Remove(joined);
            await _dbContext.SaveChangesAsync();

            var islast = _dbContext.UserGroupRelations.Exists(t => t.GroupId == group.Id);
            if (islast)
            {
                _dbContext.GroupConversations.Remove(group);
                await _dbContext.SaveChangesAsync();
            }
            return this.Protocal(ErrorType.Success, $"You have successfully leaved the group: {groupName}!");
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> InitPusher()
        {
            var user = await GetKahlaUser();
            if (user.CurrentChannel == -1 || (await _channelService.ValidateChannelAsync(user.CurrentChannel, user.ConnectKey)).Code != ErrorType.Success)
            {
                var channel = await _pusher.Init(user.Id);
                user.CurrentChannel = channel.ChannelId;
                user.ConnectKey = channel.ConnectKey;
                await _userManager.UpdateAsync(user);
            }
            var model = new InitPusherViewModel
            {
                Code = ErrorType.Success,
                Message = "Successfully get your channel.",
                ChannelId = user.CurrentChannel,
                ConnectKey = user.ConnectKey,
                ServerPath = new AiurUrl(_serviceLocation.StargateListenAddress, "Listen", "Channel", new ChannelAddressModel
                {
                    Id = user.CurrentChannel,
                    Key = user.ConnectKey
                }).ToString()
            };
            return Json(model);
        }

        public async Task<IActionResult> LogOff()
        {
            await _signInManager.SignOutAsync();
            return this.Protocal(ErrorType.Success, "Success.");
        }

        private async Task<KahlaUser> GetKahlaUser()
        {
            if (!User.Identity.IsAuthenticated)
            {
                return null;
            }
            return await _userManager.FindByNameAsync(User.Identity.Name);
        }
    }
}

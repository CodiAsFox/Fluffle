﻿using AutoMapper;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using Noppes.Fluffle.Bot.Database;
using Noppes.Fluffle.Bot.Routing;
using Noppes.Fluffle.Bot.Utils;
using Noppes.Fluffle.Configuration;
using Noppes.Fluffle.Utils;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Noppes.Fluffle.Bot.Controllers;

public class ReverseSearchController
{
    private static readonly AsyncLock MessageLock = new();

    private readonly BotConfiguration _configuration;
    private readonly ITelegramBotClient _botClient;
    private readonly BotContext _context;
    private readonly ReverseSearchScheduler _reverseSearchScheduler;
    private readonly ReverseSearchRequestLimiter _reverseSearchRequestLimiter;
    private readonly MediaGroupTracker _mediaGroupTracker;
    private readonly ILogger<ReverseSearchController> _logger;

    public ReverseSearchController(BotConfiguration configuration, ITelegramBotClient botClient, BotContext context, ReverseSearchScheduler reverseSearchScheduler, ReverseSearchRequestLimiter reverseSearchRequestLimiter, MediaGroupTracker mediaGroupTracker, ILogger<ReverseSearchController> logger)
    {
        _configuration = configuration;
        _botClient = botClient;
        _context = context;
        _reverseSearchScheduler = reverseSearchScheduler;
        _reverseSearchRequestLimiter = reverseSearchRequestLimiter;
        _mediaGroupTracker = mediaGroupTracker;
        _logger = logger;
    }

    [Update(UpdateType.EditedChannelPost)]
    public async Task HandleEditedChannelPost(Message message) => await HandleEdit(message);

    [Update(UpdateType.EditedMessage)]
    public async Task HandleEditedMessage(Message message) => await HandleEdit(message);

    private async Task HandleEdit(Message message)
    {
        // Skip any messages that do not have a photo attached
        if (message.Photo == null)
            return;

        // No idea what this type of chat is supposed to be, so we skip it
        if (message.Chat.Type == ChatType.Sender)
            return;

        var mongoMessage = await _context.Messages.FirstOrDefaultAsync(x => x.ChatId == message.Chat.Id && x.MessageId == message.MessageId);

        // Probably a message that got added before the bot had access to the chat
        if (mongoMessage == null)
            return;

        // Skip the message if the previously reverse searched photo did not change
        if (mongoMessage.FileUniqueId == message.Photo.Largest().FileUniqueId)
            return;

        var (shouldContinue, priority) = await _reverseSearchRequestLimiter.NextAsync(message.Chat.Id);
        if (!shouldContinue)
            return;

        await ReverseSearchAsync(message.Chat, message, mongoMessage, priority);
    }

    [Update(UpdateType.ChannelPost)]
    public async Task HandleChannelPost(Message message) => await HandleMessage(message);

    [Update(UpdateType.Message)]
    public async Task HandleMessage(Message message)
    {
        // Skip any messages that do not have a photo attached
        if (message.Photo == null)
            return;

        // No idea what this type of chat is supposed to be, so we skip it
        if (message.Chat.Type == ChatType.Sender)
            return;

        // Skip forwarded messages in channels, those cannot be edited and creating a channel
        // post would be an awful way of solving this issue. The only real way is to link a
        // discussion group
        if (message.Chat.Type == ChatType.Channel && (message.ForwardFrom != null || message.ForwardFromChat != null))
            return;

        // Get the chat from the database
        var chat = await _context.Chats.FirstAsync(x => x.Id == message.Chat.Id);

        // Skip messages in supergroups of which the message are forwarded from their linked channel
        if (message.Chat.Type == ChatType.Supergroup && message.ForwardFromChat != null && message.ForwardFromChat.Id == chat.LinkedChatId)
            return;

        // Skip messages that already have at least one source attached to them. Only applies to
        // channels, groups and supergroups
        if (message.Chat.Type != ChatType.Private)
        {
            if (message.CaptionEntities != null)
            {
                var hasSourcesInCaption = message.CaptionEntities
                    .Where(x => x.Url != null)
                    .Select(x => new Uri(x.Url))
                    .Any(x => _configuration.TelegramKnownSources.Any(y => x.Host.Contains(y)));

                if (hasSourcesInCaption)
                    return;
            }

            if (message.ReplyMarkup != null)
            {
                var hasSourcesInReplyMarkup = message.ReplyMarkup.InlineKeyboard
                    .SelectMany(x => x)
                    .Where(x => x.Url != null)
                    .Select(x => new Uri(x.Url))
                    .Any(x => _configuration.TelegramKnownSources.Any(y => x.Host.Contains(y)));

                if (hasSourcesInReplyMarkup)
                    return;
            }
        }

        MongoMessage mongoMessage;
        var now = DateTime.UtcNow;
        using (var _ = await MessageLock.LockAsync())
        {
            mongoMessage = new MongoMessage
            {
                ChatId = chat.Id,
                MessageId = message.MessageId,
                Caption = message.Caption,
                CaptionEntities = message.CaptionEntities,
                MediaGroupId = message.MediaGroupId,
                When = now,
                ReverseSearchFormat = chat.ReverseSearchFormat,
                TextFormat = chat.TextFormat,
                TextSeparator = chat.TextSeparator,
                InlineKeyboardFormat = chat.InlineKeyboardFormat
            };

            await _context.Messages.InsertAsync(mongoMessage);
        }

        var (shouldContinue, priority) = await _reverseSearchRequestLimiter.NextAsync(chat.Id);
        if (!shouldContinue)
            return;

        MediaGroupData mediaGroupData = null;
        MediaGroupItem mediaGroupItem = null;
        if (message.MediaGroupId != null && chat.Type is ChatType.Channel or ChatType.Group or ChatType.Supergroup)
            (mediaGroupData, mediaGroupItem) = await _mediaGroupTracker.TrackAsync(message.Chat, mongoMessage);

        await ReverseSearchAsync(message.Chat, message, mongoMessage, priority, mediaGroupData, mediaGroupItem);
    }

    private async Task HandlePrivateImage(Chat chat, Message message, MongoMessage mongoMessage, ReverseSearchResponse response)
    {
        response.FileId = message.Photo.Largest().FileId;

        await RateLimiter.RunAsync(chat, () => _botClient.DeleteMessageAsync(chat.Id, message.MessageId));

        if (mongoMessage.FluffleResponse.Results.Count == 0)
            response.Text = "This image could not be found.";
        else
            Formatter.RouteMessage(mongoMessage, response);
    }

    private Task HandleGroupImage(Chat chat, Message message, MongoMessage mongoMessage, ReverseSearchResponse response)
    {
        if (mongoMessage.FluffleResponse.Results.Count == 0)
            return Task.CompletedTask;

        Formatter.RouteMessage(mongoMessage, response);
        response.ReplyToMessageId = message.MessageId;

        return Task.CompletedTask;
    }

    private Task HandleChannelImage(Chat chat, Message message, MongoMessage mongoMessage, ReverseSearchResponse response)
    {
        var config = new MapperConfiguration(cfg => cfg.CreateMap<MessageEntity, MessageEntity>());
        var mapper = config.CreateMapper();

        response.MessageId = message.MessageId;
        response.ExistingText = mongoMessage.Caption;
        response.ExistingTextEntities = mongoMessage.CaptionEntities?.Select(x => mapper.Map(x, new MessageEntity())).ToArray();

        // Use the text format if the message is part of a media group
        if (mongoMessage.MediaGroupId != null)
            mongoMessage.ReverseSearchFormat = ReverseSearchFormat.Text;

        if (mongoMessage.FluffleResponse.Results.Count <= 0)
            return Task.CompletedTask;

        Formatter.RouteMessage(mongoMessage, response);

        return Task.CompletedTask;
    }

    private async Task ReverseSearchAsync(Chat chat, Message message, MongoMessage mongoMessage, int priority, MediaGroupData mediaGroupData = null, MediaGroupItem mediaGroupItem = null)
    {
        var photos = ImageSizeHelper.OrderByDownloadPreference(message.Photo, x => x.Width, x => x.Height, 350).ToList();

        await using var stream = new MemoryStream();
        for (var i = 0; i < photos.Count; i++)
        {
            var photo = photos[i];

            try
            {
                stream.SetLength(0);
                await _botClient.GetInfoAndDownloadFileAsync(photo.FileId, stream, new CancellationTokenSource(3000).Token);

                break;
            }
            catch (TaskCanceledException)
            {
                if (i == photos.Count - 1)
                    throw;
            }
        }
        stream.Position = 0;

        mongoMessage.FileUniqueId = message.Photo.Largest().FileUniqueId;
        mongoMessage.FluffleResponse = await _reverseSearchScheduler.ProcessAsync(new ReverseSearchSchedulerItem
        {
            Stream = stream,
            IncludeNsfw = true,
            Limit = 8
        }, priority);
        mongoMessage.FluffleResponse.Results = mongoMessage.FluffleResponse.Results
            .Where(x => x.Match == FluffleMatch.Exact)
            .OrderBy(x => x.Platform.Priority())
            .ToList();

        if (mediaGroupItem != null)
        {
            await _context.Messages.ReplaceAsync(x => x.Id == mongoMessage.Id, mongoMessage);
            mediaGroupItem.Priority = priority;
            mediaGroupItem.Image = stream.ToArray();
            mediaGroupItem.ReverseSearchEvent.Set();

            await mediaGroupData!.AllReceivedEvent.WaitAsync();
            if (!mediaGroupData.ShouldControllerContinue)
            {
                await mediaGroupData.ProcessedEvent.WaitAsync();

                return;
            }
        }

        try
        {
            var response = new ReverseSearchResponse
            {
                MessageId = mongoMessage.ResponseMessageId,
                Chat = chat,
                Photo = message.Photo.Largest()
            };

            Func<Chat, Message, MongoMessage, ReverseSearchResponse, Task> func = chat.Type switch
            {
                ChatType.Private => HandlePrivateImage,
                ChatType.Group or ChatType.Supergroup => HandleGroupImage,
                ChatType.Channel => HandleChannelImage,
                _ => throw new NotImplementedException()
            };
            await func(chat, message, mongoMessage, response);

            if (mongoMessage.ReverseSearchFormat == ReverseSearchFormat.Text)
            {
                // Use the original caption if no caption got generated
                if (response.Text == null && mongoMessage.Caption != null)
                {
                    response.Text = mongoMessage.Caption;
                    response.TextEntities = mongoMessage.CaptionEntities;
                }

                // Ignore if the effective text and URLs stayed the same
                var isTextTheSame = response.Text == (message.Text ?? message.Caption);
                var newUrls = response.TextEntities?.Select(x => x.Url) ?? Array.Empty<string>();
                var oldUrls = (message.Entities ?? message.CaptionEntities)?.Select(x => x.Url) ?? Array.Empty<string>();
                var areUrlsTheSame = newUrls.SequenceEqual(oldUrls);
                if (isTextTheSame && areUrlsTheSame)
                    return;

                // Clear caption if no reverse search results were returned
                if (response.Text == null && (message.Text ?? message.Caption) != null)
                    response.Text = string.Empty;
            }

            if (response.Text == null && mongoMessage.ReverseSearchFormat == ReverseSearchFormat.InlineKeyboard)
            {
                // We do not need to do anything if the new and existing reply markups are both null
                if (response.ReplyMarkup == null && message.ReplyMarkup == null)
                    return;

                // Check if the contents of both inline keyboards are equal and skip updating them if they are
                if (response.ReplyMarkup != null && message.ReplyMarkup != null)
                {
                    var isReplyMarkupEqual = response.ReplyMarkup.InlineKeyboard
                        .SelectMany(x => x)
                        .Select(x => (x.Text, x.Url))
                        .SequenceEqual(message.ReplyMarkup.InlineKeyboard
                            .SelectMany(y => y)
                            .Select(y => (y.Text, y.Url))
                        );

                    if (isReplyMarkupEqual)
                        return;
                }

                // Clear the reply markup if no reverse search results were returned
                if (response.ReplyMarkup == null && message.ReplyMarkup != null)
                    response.ReplyMarkup = InlineKeyboardMarkup.Empty();
            }

            // Captions are used for channels and private chats
            response.IsTextCaption = chat.Type is ChatType.Private or ChatType.Channel;

            var sentMessage = await response.Process(_botClient);
            mongoMessage.ResponseMessageId = sentMessage.MessageId;
        }
        finally
        {
            await _context.Messages.ReplaceAsync(x => x.Id == mongoMessage.Id, mongoMessage);
        }
    }
}

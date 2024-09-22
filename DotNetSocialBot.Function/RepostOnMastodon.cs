using DotNetSocialBot.FunctionApp;
using HtmlAgilityPack;
using Mastonet;
using Mastonet.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DotNetSocialBot.Function;

public class RepostOnMastodon
{
    private readonly MastodonClient _client;
    private readonly ILogger _logger;
    private Account? _currentUser;

    public RepostOnMastodon(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<RepostOnMastodon>();
        var handler = new HttpClientHandler();
#if DEBUG // TODO: find out why certs are not accepted locally
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
#endif
        _client = new MastodonClient(Config.Instance, Config.AccessToken, new HttpClient(handler));
    }

    [Function(nameof(RepostOnMastodon))]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo myTimer)
    {
        _currentUser = await _client.GetCurrentUser();

        var count = await HandleNotifications();
        count += await BoostTags();

        _logger.LogInformation("Boosted {Count} posts", count);
    }

    private async Task<int> HandleNotifications()
    {
        var notifications = await _client
            .GetNotifications(excludeTypes: NotificationType.Follow | NotificationType.Favourite | NotificationType.Reblog);

        var count = 0;

        try
        {
            foreach (var notification in notifications.Where(n => n.Status?.Account?.Bot != true))
            {
                if (!notification.Status.IsReply())
                {
                    if (await BoostDirectMention(notification))
                    {
                        count++;
                    }
                }
                else
                {
                    if (await BoostBoostRequest(notification))
                    {
                        count++;
                    }
                }
            }
        }
        finally
        {
            // trying to understand why we miss some notifications??
            // we try to dismission notification one by one to not miss any
            //_client.ClearNotifications();
            foreach(var notification in notifications)
            {
                await _client.DismissNotification(notification.Id);
            }

            if (notifications.Count > 0)
            {
                // approx, we could have missed some notification between GetNotifications() and Clear()
                _logger.LogInformation("Handled {Count} notifications", notifications.Count);
            }
        }

        return count;
    }

    private async Task<bool> BoostBoostRequest(Notification notification)
    {
        var document = new HtmlDocument();
        document.LoadHtml(notification.Status?.Content);
        var replyText = document.DocumentNode.InnerText;

        if (Config.ValidBoostRequestMessages.Any(m =>
                replyText.EndsWith(m, StringComparison.InvariantCultureIgnoreCase)))
        {
            var statusIdToBoost = notification.Status?.InReplyToId;
            if (statusIdToBoost is not null)
            {
                var statusToBoost = await _client.GetStatus(statusIdToBoost);
                if (statusToBoost.IsReply()
                    // we allow boosting bot posts, fixes #1
                    //|| statusToBoost.Account.Bot == true
                    || statusToBoost.Account.Id == _currentUser!.Id
                    || statusToBoost.Reblogged == true)
                {
                    await _client.PublishStatus("That's nothing I can boost. 😔",
                        replyStatusId: notification.Status?.Id);

                    _logger.LogInformation(
                        "Denied boost request from @{Account} on {PostTime} ({IsReply}; {StatusAccountId}; {CurrentUserId}; {Reblogged}",
                        notification.Account.AccountName,
                        notification.Status?.CreatedAt,
                        statusToBoost.IsReply(),
                        statusToBoost.Account.Id,
                        _currentUser!.Id,
                        statusToBoost.Reblogged
                        );

                    return false;
                }
                else
                {
                    await _client.Reblog(statusToBoost.Id);
                    _logger.LogInformation
                    ("Boosted post from @{Account} from {PostTime}, requested by @{RequesterAccount} at {RequestTime}",
                        statusToBoost.Account.AccountName,
                        statusToBoost.CreatedAt,
                        notification.Account.AccountName,
                        notification.Status?.CreatedAt);

                    return true;
                }
            }
            else
            {
                _logger.LogWarning("No original message found to boost from request by @{RequesterAccount} at {RequestTime}",
                    notification.Account.AccountName,
                    notification.Status?.CreatedAt);
                return false;
            }
        }
        else
        {
            _logger.LogInformation("No boost request message detected on message from @{Account} on {PostTime}",
                notification.Account.AccountName,
                notification.Status?.CreatedAt);

            return false;
        }
    }

    private async Task<bool> BoostDirectMention(Notification notification)
    {
        var statusId = notification.Status?.Id;
        if (statusId is null)
        {
            _logger.LogError(
                "Could not determine ID of status that mentioned me, ignoring post by @{Account} from {PostTime}",
                notification.Status?.Account.AccountName,
                notification.Status?.CreatedAt);
            return false;
        }

        var statusVisibility = notification.Status?.Visibility;
        if (statusVisibility is null)
        {
            _logger.LogError(
                "Could not determine visibility of status that mentioned me, ignoring post by @{Account} from {PostTime}",
                notification.Status?.Account.AccountName,
                notification.Status?.CreatedAt);
            return false;
        }

        if (statusVisibility == Visibility.Direct)
        {
            _logger.LogInformation(
                "Ignoring direct message post by @{Account} from {PostTime}",
                notification.Status?.Account.AccountName,
                notification.Status?.CreatedAt);
            return false;
        }

        await _client.Reblog(statusId);
        _logger.LogInformation("Boosted post that mentioned me by @{Account} from {PostTime}",
            notification.Account.AccountName,
            notification.Status?.CreatedAt);
        return true;
    }

    private async Task<int> BoostTags()
    {
        var followedTags = await _client.ViewFollowedTags();
        if (!followedTags.Any()) return 0;

        var count = 0;

        foreach (var status in (await _client.GetHomeTimeline(new ArrayOptions { Limit = 100 })).Where(s =>
                     !s.IsReply()
                     && s.Reblogged != true
                     && s.Reblog is null
                     && s.Account.Bot != true
                     && s.Account.Id != _currentUser!.Id))
        {
            var followedTagsInPost = status.Tags.Select(t => t.Name)
                .Intersect(followedTags.Select(t => t.Name), StringComparer.OrdinalIgnoreCase).ToList();

            await _client.Reblog(status.Id);
            _logger.LogInformation(
                "Boosted hash-tagged post by @{Account} from {PostTime} because of followed hashtags {FollowedTagsInPost}",
                status.Account.AccountName,
                status.CreatedAt,
                followedTagsInPost);
            count++;
        }

        return count;
    }
}

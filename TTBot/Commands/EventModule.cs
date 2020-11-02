﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using TTBot.DataAccess;
using TTBot.Extensions;
using TTBot.Models;
using TTBot.Services;

namespace TTBot.Commands
{
    [Group("event")]
    [Alias("events")]
    public class EventModule : ModuleBase<SocketCommandContext>
    {
        private readonly IEvents _events;
        private readonly IPermissionService _permissionService;
        private readonly IEventSignups _eventSignups;
        private readonly IConfirmationChecks _confirmationChecks;
        private readonly IConfirmationCheckPrinter _confirmationCheckPrinter;

        public EventModule(IEvents events, IPermissionService permissionService, IEventSignups eventSignups, IConfirmationChecks confirmationChecks, IConfirmationCheckPrinter confirmationCheckPrinter)
        {
            _events = events;
            _permissionService = permissionService;
            _eventSignups = eventSignups;
            _confirmationChecks = confirmationChecks;
            _confirmationCheckPrinter = confirmationCheckPrinter;
        }

        [Command("create")]
        [Alias("add")]
        public async Task Create(string eventName, string shortName, int? capacity = null)
        {
            var author = Context.Message.Author as SocketGuildUser;
            if (!await _permissionService.UserIsModeratorAsync(Context, author))
            {
                await Context.Channel.SendMessageAsync("You dont have permission to create events");
                return;
            }

            var existingEvent = await _events.GetActiveEvent(eventName, Context.Guild.Id, Context.Channel.Id);
            var existingEventWithAlias = await _events.GetActiveEvent(shortName, Context.Guild.Id, Context.Channel.Id);
            if (existingEvent != null || existingEventWithAlias != null)
            {
                await Context.Channel.SendMessageAsync("There is already an active event with that name or short name for this channel. Event names must be unique!");
                return;
            }
            var @event = new Models.Event
            {
                ChannelId = Context.Channel.Id.ToString(),
                GuildId = Context.Guild.Id.ToString(),
                ShortName = shortName,
                Closed = false,
                Name = eventName,
                Capacity = capacity
            };
            await _events.SaveAsync(@event);

            await Context.Channel.SendMessageAsync($"{Context.Message.Author.Mention} has created the event {eventName}! Sign up to the event by typing `!event join {@event.DisplayName}` in this channel. If you've signed up and can no longer attend, use the command `!event unsign {@event.DisplayName}`");
        }

        [Command("close")]
        [Alias("delete")]
        public async Task Close([Remainder] string eventName)
        {
            var author = Context.Message.Author as SocketGuildUser;
            if (!await _permissionService.UserIsModeratorAsync(Context, author))
            {
                await Context.Channel.SendMessageAsync("You dont have permission to create events");
                return;
            }

            var existingEvent = await _events.GetActiveEvent(eventName, Context.Guild.Id, Context.Channel.Id);
            if (existingEvent == null)
            {
                await Context.Channel.SendMessageAsync($"Unable to find an active event with the name {eventName}");
                return;
            }

            existingEvent.Closed = true;
            await _events.SaveAsync(existingEvent);

            await Context.Channel.SendMessageAsync($"{existingEvent.Name} is now closed!");
        }

        [Command("active")]
        [Alias("current", "open")]
        public async Task ActiveEvents()
        {
            var activeEvents = await _events.GetActiveEvents(Context.Guild.Id, Context.Channel.Id);
            if (!activeEvents.Any())
            {
                await Context.Channel.SendMessageAsync($"There's no events currently running for {Discord.MentionUtils.MentionChannel(Context.Channel.Id)}.");
            }
            else
            {
                await Context.Channel.SendMessageAsync($"Currently active events:{Environment.NewLine}{string.Join(Environment.NewLine, activeEvents.Select(ev => $"{ev.Name}{(ev.SpaceLimited ? $" - {ev.ParticipantCount}/{ev.Capacity} participants" : "")}"))}");
                await Context.Channel.SendMessageAsync($"Join any active event with the command `!event signup event name`");
            }
        }

        [Command("signup")]
        [Alias("sign", "join")]
        public async Task SignUp([Remainder] string eventName)
        {
            var existingEvent = await _events.GetActiveEvent(eventName, Context.Guild.Id, Context.Channel.Id);
            if (existingEvent == null)
            {
                await Context.Channel.SendMessageAsync($"Unable to find an active event with the name {eventName}");
                return;
            }
            if (await _eventSignups.GetSignUp(existingEvent, Context.Message.Author) != null)
            {
                await Context.Channel.SendMessageAsync($"You're already signed up to {eventName}");
                return;
            }

            if (existingEvent.SpaceLimited && existingEvent.ParticipantCount >= existingEvent.Capacity)
            {
                await Context.Channel.SendMessageAsync($"Sorry, but {eventName} is already full! Keep an eye out in-case someone pulls out.");
                return;
            }
            await _eventSignups.AddUserToEvent(existingEvent, Context.Message.Author as SocketGuildUser);
            await Context.Channel.SendMessageAsync($"Thanks {Context.Message.Author.Mention}! You've been signed up to {existingEvent.Name}. ");
            await GetSignups(eventName);
            await UpdateConfirmationCheckForEvent(existingEvent);
        }

        [Command("unsign")]
        [Alias("unsignup")]
        public async Task Unsign([Remainder] string eventName)
        {
            var existingEvent = await _events.GetActiveEvent(eventName, Context.Guild.Id, Context.Channel.Id);
            if (existingEvent == null)
            {
                await Context.Channel.SendMessageAsync($"Unable to find an active event with the name {eventName}");
                return;
            }
            var existingSignup = await _eventSignups.GetSignUp(existingEvent, Context.Message.Author);
            if (existingSignup == null)
            {
                await Context.Channel.SendMessageAsync($"You're not currently signed up to {eventName}");
                return;
            }
            await _eventSignups.Delete(existingSignup);
            await Context.Channel.SendMessageAsync($"Thanks { Context.Message.Author.Mention}! You're no longer signed up to {existingEvent.Name}.");
            await GetSignups(eventName);
            await UpdateConfirmationCheckForEvent(existingEvent);
        }

        [Command("signups")]
        [Alias("participants")]
        public async Task GetSignups([Remainder] string eventName)
        {
            var existingEvent = await _events.GetActiveEvent(eventName, Context.Guild.Id, Context.Channel.Id);
            if (existingEvent == null)
            {
                await Context.Channel.SendMessageAsync($"Unable to find an active event with the name {eventName}");
                return;
            }

            var signUps = await _eventSignups.GetAllSignupsForEvent(existingEvent);
            var users = await Task.WhenAll(signUps.Select(async sup => (await Context.Channel.GetUserAsync(Convert.ToUInt64(sup.UserId)) as SocketGuildUser)));

            var usersInEvent = string.Join(Environment.NewLine, users.Select(u => u.GetDisplayName()));
            var message = $"**{existingEvent.Name}**{Environment.NewLine}";
            if (existingEvent.SpaceLimited)
            {
                message += $"There's {users.Length} out of {existingEvent.Capacity}";
            }
            else
            {
                message += $"There's {users.Length}";
            }

            message += $" racers signed up for {eventName}.{Environment.NewLine}{ usersInEvent}{Environment.NewLine}{Environment.NewLine}Sign up to this event with `!event join {existingEvent.DisplayName}`";

            await Context.Channel.SendMessageAsync(message);
        }

        [Command("bulkadd", ignoreExtraArgs: true)]
        [Alias("bulksign")]
        public async Task BulkAdd(string eventName)
        {
            var author = Context.Message.Author as SocketGuildUser;
            if (!await _permissionService.UserIsModeratorAsync(Context, author))
            {
                await Context.Channel.SendMessageAsync("You dont have permission to bulk add");
                return;
            }

            var existingEvent = await _events.GetActiveEvent(eventName, Context.Guild.Id, Context.Channel.Id);
            if (existingEvent == null)
            {
                await Context.Channel.SendMessageAsync($"Unable to find an active event with the name {eventName}");
                return;
            }

            await Task.WhenAll(Context.Message.MentionedUsers.Select(async user => await _eventSignups.AddUserToEvent(existingEvent, user)));
            await GetSignups(eventName);
            await UpdateConfirmationCheckForEvent(existingEvent);
        }

        [Command("remove", ignoreExtraArgs: true)]
        public async Task Remove(string eventName)
        {
            var existingEvent = await _events.GetActiveEvent(eventName, Context.Guild.Id, Context.Channel.Id);
            if (existingEvent == null)
            {
                await Context.Channel.SendMessageAsync($"Unable to find an active event with the name {eventName}");
                return;
            }

            foreach (var user in Context.Message.MentionedUsers)
            {
                var existingSignup = await _eventSignups.GetSignUp(existingEvent, user);
                if (existingSignup != null)
                {
                    await _eventSignups.Delete(existingSignup);
                }
            }

            await Context.Channel.SendMessageAsync($"Removed {string.Join(' ', Context.Message.MentionedUsers.Select(user => user.Username))} from {eventName}");
            await GetSignups(eventName);
            await UpdateConfirmationCheckForEvent(existingEvent);
        }

        [Command("help")]
        public async Task Help()
        {
            await Context.Channel.SendMessageAsync("Use `!events active` to see a list of all active events. To join an event use the `!event join` command with the name of the event. " +
                "For example `!event join ACC Championship`. To unsign from an event, use the `!event unsign` command with the name of the event. For example, `!event unsign ACC Championship`.");
        }

        [Command("confirm")]
        public async Task Confirm([Remainder] string eventName)
        {
            if (!await _permissionService.AuthorIsModerator(Context))
            {
                return;
            }

            var existingEvent = await _events.GetActiveEvent(eventName, Context.Guild.Id, Context.Channel.Id);
            if (existingEvent == null)
            {
                await Context.Channel.SendMessageAsync($"Unable to find an active event with the name {eventName}");
                return;
            }

            var message = await Context.Channel.SendMessageAsync("Starting Confirmation Check..");
            await _confirmationChecks.SaveAsync(new ConfirmationCheck()
            {
                EventId = existingEvent.Id,
                MessageId = message.Id.ToString()
            });
            await _confirmationCheckPrinter.WriteMessage(this.Context.Channel, message, existingEvent);
        }

        private async Task UpdateConfirmationCheckForEvent(EventsWithCount @event)
        {
            var confirmationCheck = await _confirmationChecks.GetMostRecentConfirmationCheckForEventAsync(@event.Id);
            if (confirmationCheck == null)
            {
                return;
            }
            var message = await Context.Channel.GetMessageAsync(Convert.ToUInt64(confirmationCheck.MessageId));
            if (message == null)
            {
                return;
            }
            await _confirmationCheckPrinter.WriteMessage(Context.Channel, (IUserMessage)message, @event);
        }
    }
}
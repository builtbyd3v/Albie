using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie opens a conversation ("open my chat with Adrian", "pull up #general": the hub's messages_open tool). The hub's
/// ws <c>messages.open {pet_id, request_id, channel, channel_name, kind}</c> shows it on the Slack panel, opening MSG mode
/// when it is closed (like tapping a Mentions card does), and this answers <c>messages.opened {pet_id, request_id,
/// channel, ok, reason?}</c> in the same frame, so Albie says what really happened. reason: <c>not_at_desk</c> (the
/// Workspace only opens at a desk) or <c>no_workspace</c>. A conversation the list doesn't have yet (a DM the hub just
/// opened) gets a row right away (<see cref="SlackStore.Provisional"/>). Installs itself on scene load into any scene
/// with a DeskPresence, like <see cref="DraftPresenter"/>.
/// </summary>
public class SlackRemote : MonoBehaviour
{
    const string OpenType = "messages.open", OpenedType = "messages.opened", MessagesMode = "messages";

    IDisposable subscription;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<SlackRemote>() != null || FindAnyObjectByType<DeskPresence>() == null) return;
        new GameObject("MessagesRemote").AddComponent<SlackRemote>();
    }

    void OnEnable() => subscription = ForgeNet.On<SlackOpenRequest>(OpenType, OnOpen);

    void OnDisable()
    {
        subscription?.Dispose();
        subscription = null;
    }

    void OnOpen(SlackOpenRequest req)
    {
        if (req == null || string.IsNullOrEmpty(req.channel)) return;
        SlackStore.Provisional(req.channel, req.channelName, req.kind);
        string reason = null;
        var ws = Workspace.Current;
        if (SlackPanel.Active != null) SlackPanel.Active.Open(req.channel);
        else if (ws == null) reason = "no_workspace";
        else if (ws.presence == null || !ws.presence.AtDesk) reason = "not_at_desk";
        else
        {
            SlackPanel.PendingChannel = req.channel;  // the panel opens it as it comes up
            ws.Open(MessagesMode);
            if (ws.ActiveMode != MessagesMode) reason = "not_at_desk";
        }

        var answer = new JObject
        {
            ["pet_id"] = string.IsNullOrEmpty(req.petId) ? "bit" : req.petId,
            ["request_id"] = req.requestId ?? "",
            ["channel"] = req.channel,
            ["ok"] = reason == null,
        };
        if (reason != null)
        {
            answer["reason"] = reason;
            if (SlackPanel.Active == null) SlackPanel.PendingChannel = null;
        }
        ForgeNet.Send(OpenedType, answer);
        Debug.Log($"[SlackRemote] Albie opened {req.channelName ?? req.channel}: {(reason ?? "ok")}.");
    }
}

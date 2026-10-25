using System;
using UnityEngine;

/// <summary>
/// Albie opens a conversation ("open my chat with Adrian", "pull up #general": the hub's messages_open tool). The hub's
/// ws <c>messages.open {pet_id, request_id, channel, channel_name, kind}</c> shows it on its provider's panel (Slack, or
/// iMessage for an <c>im:</c> chat; <see cref="SlackPanel.ForChannel"/>), opening MSG mode when it is closed (like tapping
/// a Mentions card does), and this answers <c>messages.opened {pet_id, request_id, channel, ok, reason?}</c> in the same frame, so Albie says what really happened. reason: <c>not_at_desk</c> (the
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
        var panel = SlackPanel.ForChannel(req.channel);
        if (panel != null) panel.Open(req.channel);
        else if (ws == null) reason = "no_workspace";
        else if (ws.presence == null || !ws.presence.AtDesk) reason = "not_at_desk";
        else
        {
            SlackPanel.PendingChannel = req.channel;  // the panel opens it as it comes up
            ws.Open(MessagesMode);
            if (ws.ActiveMode != MessagesMode) reason = "not_at_desk";
        }

        if (reason != null && panel == null) SlackPanel.PendingChannel = null;
        ForgeNet.Send(OpenedType, new SlackOpened
        {
            petId = string.IsNullOrEmpty(req.petId) ? "bit" : req.petId,
            requestId = req.requestId ?? "",
            channel = req.channel,
            ok = reason == null,
            reason = reason,
        });
        Debug.Log($"[SlackRemote] Albie opened {req.channelName ?? req.channel}: {(reason ?? "ok")}.");
    }
}

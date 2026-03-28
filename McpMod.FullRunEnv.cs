using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using MegaCrit.Sts2.Core.Simulation;

namespace STS2_MCP;

public static partial class McpMod
{
    private static void HandleGetFullRunEnvState(HttpListenerResponse response)
    {
        try
        {
            var stateTask = RunOnMainThread(BuildFullRunEnvState);
            var state = stateTask.GetAwaiter().GetResult();
            SendJson(response, state);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Failed to read full run env state: {ex.Message}");
        }
    }

    private static void HandlePostFullRunEnvReset(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            var parsed = ParseFullRunEnvRequestObject(request, allowEmptyBody: true);

            if (IsFullRunSimulatorActive())
            {
                var resetReq = ParseSimResetRequest(parsed);
                var snapshotTask = RunOnMainThreadAsync(() => FullRunTrainingEnvService.Instance.ResetAsync(resetReq));
                snapshotTask.GetAwaiter().GetResult();

                var stateTask = RunOnMainThread(BuildFullRunEnvState);
                var state = stateTask.GetAwaiter().GetResult();
                SendJson(response, state);
                return;
            }

            var startResultTask = RunOnMainThread(() => ExecuteStartRun(parsed));
            var startResult = startResultTask.GetAwaiter().GetResult();
            if (IsErrorResult(startResult, out var resetError))
                throw new InvalidOperationException(resetError ?? "Failed to start run.");

            var legacyState = WaitForFullRunEnvState(
                predicate: static state => GetStateType(state) != "menu" && IsSettledFullRunState(state) && IsActionableOrTerminalFullRunState(state),
                timeoutMs: GetOptionalInt(parsed, "timeout_ms", 20000),
                pollDelayMs: GetOptionalInt(parsed, "poll_delay_ms", 50));
            SendJson(response, legacyState);
        }
        catch (JsonException ex)
        {
            SendError(response, 400, $"Invalid JSON: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            SendError(response, 400, ex.Message);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Full run env reset failed: {ex.Message}");
        }
    }

    private static void HandlePostFullRunEnvStep(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            var parsed = ParseFullRunEnvRequestObject(request, allowEmptyBody: false);
            if (!parsed.TryGetValue("action", out var actionElem) || actionElem.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Missing 'action' field.");

            if (IsFullRunSimulatorActive())
            {
                var actionReq = ParseSimActionRequest(parsed);
                var simResultTask = RunOnMainThreadAsync(() => FullRunTrainingEnvService.Instance.StepAsync(actionReq));
                var simResult = simResultTask.GetAwaiter().GetResult();

                var stateTask = RunOnMainThread(BuildFullRunEnvState);
                var state = stateTask.GetAwaiter().GetResult();
                SendJson(response, ShapeFullRunEnvStepResult(state, simResult.Accepted, simResult.Error, null));
                return;
            }

            var beforeStateTask = RunOnMainThread(BuildFullRunEnvState);
            var beforeState = beforeStateTask.GetAwaiter().GetResult();

            var action = actionElem.GetString() ?? string.Empty;
            var resultTask = RunOnMainThread(() => ExecuteAction(action, parsed));
            var actionResult = resultTask.GetAwaiter().GetResult();

            var accepted = !IsErrorResult(actionResult, out var actionError);
            Dictionary<string, object?> state2;
            string? stepInfoCode = null;
            if (accepted)
            {
                try
                {
                    state2 = WaitForChangedFullRunEnvState(
                        beforeState,
                        timeoutMs: GetOptionalInt(parsed, "timeout_ms", 2000),
                        pollDelayMs: GetOptionalInt(parsed, "poll_delay_ms", 25));
                }
                catch (TimeoutException)
                {
                    state2 = RunOnMainThread(BuildFullRunEnvState).GetAwaiter().GetResult();
                    stepInfoCode = "state_change_timeout";
                }
            }
            else
            {
                state2 = RunOnMainThread(BuildFullRunEnvState).GetAwaiter().GetResult();
            }

            SendJson(response, ShapeFullRunEnvStepResult(state2, accepted, actionError, stepInfoCode));
        }
        catch (JsonException ex)
        {
            SendError(response, 400, $"Invalid JSON: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            SendError(response, 400, ex.Message);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Full run env step failed: {ex.Message}");
        }
    }

    private static Dictionary<string, JsonElement> ParseFullRunEnvRequestObject(HttpListenerRequest request, bool allowEmptyBody)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        if (string.IsNullOrWhiteSpace(body))
        {
            if (allowEmptyBody)
                return new Dictionary<string, JsonElement>();
            throw new JsonException("Request body must be a JSON object.");
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)
            ?? throw new JsonException("Request body must be a JSON object.");
    }

    private static Dictionary<string, object?> WaitForFullRunEnvState(
        Func<Dictionary<string, object?>, bool> predicate,
        int timeoutMs,
        int pollDelayMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs));
        var delay = Math.Max(10, pollDelayMs);
        Dictionary<string, object?>? lastState = null;

        while (DateTime.UtcNow <= deadline)
        {
            var stateTask = RunOnMainThread(BuildFullRunEnvState);
            lastState = stateTask.GetAwaiter().GetResult();
            if (predicate(lastState))
                return lastState;
            Thread.Sleep(delay);
        }

        throw new TimeoutException("Timed out waiting for full run env state transition.");
    }

    private static Dictionary<string, object?> WaitForChangedFullRunEnvState(
        Dictionary<string, object?> previousState,
        int timeoutMs,
        int pollDelayMs)
    {
        var previousSignature = GetFullRunStateSignature(previousState);
        var previousStateType = GetStateType(previousState);
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs));
        var delay = Math.Max(10, pollDelayMs);
        Dictionary<string, object?>? lastChangedState = null;
        string? lastChangedSignature = null;
        int stablePolls = 0;

        while (DateTime.UtcNow <= deadline)
        {
            var stateTask = RunOnMainThread(BuildFullRunEnvState);
            var state = stateTask.GetAwaiter().GetResult();
            var signature = GetFullRunStateSignature(state);
            if (!string.Equals(signature, previousSignature, StringComparison.Ordinal))
            {
                lastChangedState = state;

                if (string.Equals(signature, lastChangedSignature, StringComparison.Ordinal))
                    stablePolls++;
                else
                {
                    lastChangedSignature = signature;
                    stablePolls = 1;
                }

                if (IsFullRunTerminalState(state, ExtractFullRunOutcome(state)))
                    return state;

                if (IsSettledFullRunState(state) && IsActionableOrTerminalFullRunState(state))
                {
                    if (ShouldReturnImmediatelyForChangedFullRunState(previousState, previousStateType, state))
                        return state;

                    if (stablePolls >= 2)
                        return state;
                }
            }

            Thread.Sleep(delay);
        }

        if (lastChangedState != null)
            return lastChangedState;

        throw new TimeoutException("Timed out waiting for changed full run env state.");
    }

    private static Dictionary<string, object?> BuildFullRunEnvState()
    {
        var state = BuildGameState();
        var outcome = ExtractFullRunOutcome(state);

        // In simulator mode, use the simulator's authoritative LegalActions.
        // The HTTP state builder computes legal_actions independently from game
        // objects and can disagree with what the simulator actually accepts
        // (e.g., Neow event post-selection shows stale unchosen options).
        if (IsFullRunSimulatorActive())
            state["legal_actions"] = BuildSimulatorLegalActions();
        else
            state["legal_actions"] = BuildFullRunLegalActions(state);

        state["run_outcome"] = outcome;
        state["terminal"] = IsFullRunTerminalState(state, outcome);
        return state;
    }

    /// <summary>
    /// Converts the simulator snapshot's LegalActions into the HTTP dict format.
    /// Must be called on the Godot main thread (already inside RunOnMainThread).
    /// </summary>
    private static List<Dictionary<string, object?>> BuildSimulatorLegalActions()
    {
        try
        {
            var snapshot = FullRunTrainingEnvService.Instance.GetState();
            var result = new List<Dictionary<string, object?>>(snapshot.LegalActions.Count);
            foreach (var a in snapshot.LegalActions)
            {
                var d = new Dictionary<string, object?> { ["action"] = a.Action };
                if (a.Index.HasValue)     d["index"]     = a.Index.Value;
                if (a.Col.HasValue)       d["col"]       = a.Col.Value;
                if (a.Row.HasValue)       d["row"]       = a.Row.Value;
                if (a.CardIndex.HasValue) d["card_index"] = a.CardIndex.Value;
                if (a.Slot.HasValue)      d["slot"]      = a.Slot.Value;
                if (a.TargetId.HasValue)  d["target_id"] = a.TargetId.Value;
                if (!string.IsNullOrEmpty(a.Target)) d["target"] = a.Target;
                if (!string.IsNullOrEmpty(a.Label))  d["label"]  = a.Label;
                result.Add(d);
            }
            return result;
        }
        catch
        {
            return new List<Dictionary<string, object?>>();
        }
    }

    private static Dictionary<string, object?> ShapeFullRunEnvStepResult(
        Dictionary<string, object?> state,
        bool accepted,
        string? error,
        string? stepInfoCode = null)
    {
        var outcome = ExtractFullRunOutcome(state);
        var done = IsFullRunTerminalState(state, outcome);
        double reward = 0.0;
        if (done)
        {
            reward = outcome == "victory" || outcome == "win" ? 1.0 : -1.0;
        }

        return new Dictionary<string, object?>
        {
            ["accepted"] = accepted,
            ["error"] = error,
            ["state"] = state,
            ["reward"] = reward,
            ["done"] = done,
            ["info"] = new Dictionary<string, object?>
            {
                ["state_type"] = GetStateType(state),
                ["run_outcome"] = outcome,
                ["step_info_code"] = stepInfoCode
            }
        };
    }

    private static List<Dictionary<string, object?>> BuildFullRunLegalActions(Dictionary<string, object?> state)
    {
        var actions = new List<Dictionary<string, object?>>();
        var stateType = GetStateType(state);

        switch (stateType)
        {
            case "menu":
                AppendMenuLegalActions(actions, state);
                break;
            case "map":
                AppendIndexedLegalActions(actions, state, "map", "next_options", "choose_map_node");
                break;
            case "combat_rewards":
                AppendIndexedLegalActions(actions, state, "rewards", "items", "claim_reward");
                AppendProceedIfEnabled(actions, state, "rewards");
                break;
            case "card_reward":
                AppendCardRewardLegalActions(actions, state);
                break;
            case "rest_site":
                AppendIndexedLegalActions(actions, state, "rest_site", "options", "choose_rest_option", enabledKey: "is_enabled");
                AppendProceedIfEnabled(actions, state, "rest_site");
                break;
            case "event":
                AppendEventLegalActions(actions, state);
                break;
            case "shop":
                AppendShopLegalActions(actions, state);
                break;
            case "card_select":
                AppendCardSelectLegalActions(actions, state);
                break;
            case "relic_select":
                AppendIndexedLegalActions(actions, state, "relic_select", "relics", "select_relic");
                if (TryGetDict(state, "relic_select", out var relicSelectState))
                    AppendIfTrue(actions, relicSelectState, "can_skip", new Dictionary<string, object?> { ["action"] = "skip_relic_selection" });
                break;
            case "treasure":
                AppendIndexedLegalActions(actions, state, "treasure", "relics", "claim_treasure_relic");
                AppendProceedIfEnabled(actions, state, "treasure");
                break;
            case "monster":
            case "elite":
            case "boss":
                AppendCombatLegalActions(actions, state);
                break;
            case "hand_select":
                AppendHandSelectLegalActions(actions, state);
                break;
            case "overlay":
            case "game_over":
                AppendOverlayLegalActions(actions, state);
                break;
        }

        return actions;
    }

    private static void AppendMenuLegalActions(List<Dictionary<string, object?>> actions, Dictionary<string, object?> state)
    {
        if (!TryGetDict(state, "menu", out var menu))
            return;

        foreach (var character in EnumerateDictionaries(menu.TryGetValue("available_characters", out var rawChars) ? rawChars : null))
        {
            if (GetBool(character, "is_locked"))
                continue;
            if (TryGetString(character, "id", out var characterId))
            {
                actions.Add(new Dictionary<string, object?>
                {
                    ["action"] = "select_character",
                    ["character_id"] = characterId
                });
            }
        }

        if (GetBool(menu, "character_select_visible"))
        {
            var maxAscension = Math.Max(0, GetInt(menu, "max_ascension", 0));
            for (int ascension = 0; ascension <= maxAscension; ascension++)
            {
                actions.Add(new Dictionary<string, object?>
                {
                    ["action"] = "set_ascension",
                    ["ascension"] = ascension
                });
            }
        }

        if (GetBool(menu, "can_start"))
        {
            var startAction = new Dictionary<string, object?>
            {
                ["action"] = "start_run",
                ["ascension"] = GetInt(menu, "ascension", 0)
            };
            if (TryGetString(menu, "selected_character", out var selectedCharacter))
                startAction["character_id"] = selectedCharacter;
            actions.Add(startAction);
        }
    }

    private static void AppendCardRewardLegalActions(List<Dictionary<string, object?>> actions, Dictionary<string, object?> state)
    {
        if (!TryGetDict(state, "card_reward", out var rewardState))
            return;

        foreach (var card in EnumerateDictionaries(rewardState.TryGetValue("cards", out var rawCards) ? rawCards : null))
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action"] = "select_card_reward",
                ["card_index"] = GetInt(card, "index", -1)
            });
        }

        AppendIfTrue(actions, rewardState, "can_skip", new Dictionary<string, object?> { ["action"] = "skip_card_reward" });
    }

    private static void AppendEventLegalActions(List<Dictionary<string, object?>> actions, Dictionary<string, object?> state)
    {
        if (!TryGetDict(state, "event", out var eventState))
            return;

        if (GetBool(eventState, "in_dialogue"))
        {
            actions.Add(new Dictionary<string, object?> { ["action"] = "advance_dialogue" });
            return;
        }

        foreach (var option in EnumerateDictionaries(eventState.TryGetValue("options", out var rawOptions) ? rawOptions : null))
        {
            if (GetBool(option, "is_locked") || GetBool(option, "was_chosen"))
                continue;

            actions.Add(new Dictionary<string, object?>
            {
                ["action"] = "choose_event_option",
                ["index"] = GetInt(option, "index", -1)
            });
        }
    }

    private static void AppendShopLegalActions(List<Dictionary<string, object?>> actions, Dictionary<string, object?> state)
    {
        if (!TryGetDict(state, "shop", out var shopState))
            return;

        foreach (var item in EnumerateDictionaries(shopState.TryGetValue("items", out var rawItems) ? rawItems : null))
        {
            if (!GetBool(item, "is_stocked", defaultValue: true))
                continue;
            if (!GetBool(item, "can_afford", defaultValue: true))
                continue;
            actions.Add(new Dictionary<string, object?>
            {
                ["action"] = "shop_purchase",
                ["index"] = GetInt(item, "index", -1)
            });
        }

        AppendProceedIfEnabled(actions, state, "shop");
    }

    private static void AppendCardSelectLegalActions(List<Dictionary<string, object?>> actions, Dictionary<string, object?> state)
    {
        if (!TryGetDict(state, "card_select", out var selectState))
            return;

        foreach (var card in EnumerateDictionaries(selectState.TryGetValue("cards", out var rawCards) ? rawCards : null))
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action"] = "select_card",
                ["card_index"] = GetInt(card, "index", -1)
            });
        }

        AppendIfTrue(actions, selectState, "can_confirm", new Dictionary<string, object?> { ["action"] = "confirm_selection" });
        AppendIfTrue(actions, selectState, "can_cancel", new Dictionary<string, object?> { ["action"] = "cancel_selection" });
    }

    private static void AppendCombatLegalActions(List<Dictionary<string, object?>> actions, Dictionary<string, object?> state)
    {
        if (!TryGetDict(state, "battle", out var battleState))
            return;
        if (!TryGetDict(battleState, "player", out var playerState))
            return;

        var enemies = EnumerateDictionaries(battleState.TryGetValue("enemies", out var rawEnemies) ? rawEnemies : null)
            .Where(enemy => GetBool(enemy, "is_alive", defaultValue: true))
            .ToList();

        foreach (var card in EnumerateDictionaries(playerState.TryGetValue("hand", out var rawHand) ? rawHand : null))
        {
            if (!GetBool(card, "can_play"))
                continue;

            var handIndex = GetInt(card, "index", -1);
            var targetType = GetString(card, "target_type");
            bool requiresTarget = targetType is "enemy" or "anyenemy" or "any_enemy";
            if (requiresTarget)
            {
                foreach (var enemy in enemies)
                {
                    string? targetId = null;
                    if (TryGetString(enemy, "entity_id", out var entityId))
                        targetId = entityId;
                    else if (TryGetString(enemy, "id", out var fallbackId))
                        targetId = fallbackId;

                    if (string.IsNullOrWhiteSpace(targetId))
                        continue;

                    actions.Add(new Dictionary<string, object?>
                    {
                        ["action"] = "play_card",
                        ["card_index"] = handIndex,
                        ["target"] = targetId
                    });
                }
            }
            else
            {
                actions.Add(new Dictionary<string, object?>
                {
                    ["action"] = "play_card",
                    ["card_index"] = handIndex
                });
            }
        }

        actions.Add(new Dictionary<string, object?> { ["action"] = "end_turn" });
    }

    private static void AppendHandSelectLegalActions(List<Dictionary<string, object?>> actions, Dictionary<string, object?> state)
    {
        if (!TryGetDict(state, "hand_select", out var handSelectState))
            return;

        foreach (var card in EnumerateDictionaries(handSelectState.TryGetValue("cards", out var rawCards) ? rawCards : null))
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action"] = "combat_select_card",
                ["card_index"] = GetInt(card, "index", -1)
            });
        }

        AppendIfTrue(actions, handSelectState, "can_confirm", new Dictionary<string, object?> { ["action"] = "combat_confirm_selection" });
    }

    private static void AppendOverlayLegalActions(List<Dictionary<string, object?>> actions, Dictionary<string, object?> state)
    {
        var containerKey = GetStateType(state) == "game_over" ? "game_over" : "overlay";
        if (!TryGetDict(state, containerKey, out var overlayState))
            return;

        foreach (var button in EnumerateDictionaries(overlayState.TryGetValue("buttons", out var rawButtons) ? rawButtons : null))
        {
            if (!GetBool(button, "is_enabled", defaultValue: true))
                continue;

            actions.Add(new Dictionary<string, object?>
            {
                ["action"] = "overlay_press",
                ["index"] = GetInt(button, "index", -1)
            });
        }
    }

    private static void AppendIndexedLegalActions(
        List<Dictionary<string, object?>> actions,
        Dictionary<string, object?> state,
        string containerKey,
        string collectionKey,
        string actionName,
        string enabledKey = "")
    {
        if (!TryGetDict(state, containerKey, out var container))
            return;

        foreach (var item in EnumerateDictionaries(container.TryGetValue(collectionKey, out var rawItems) ? rawItems : null))
        {
            if (!string.IsNullOrWhiteSpace(enabledKey) && !GetBool(item, enabledKey, defaultValue: true))
                continue;
            actions.Add(new Dictionary<string, object?>
            {
                ["action"] = actionName,
                ["index"] = GetInt(item, "index", -1)
            });
        }
    }

    private static void AppendProceedIfEnabled(List<Dictionary<string, object?>> actions, Dictionary<string, object?> state, string containerKey)
    {
        if (!TryGetDict(state, containerKey, out var container))
            return;
        AppendIfTrue(actions, container, "can_proceed", new Dictionary<string, object?> { ["action"] = "proceed" });
    }

    private static void AppendIfTrue(List<Dictionary<string, object?>> actions, Dictionary<string, object?> state, string key, Dictionary<string, object?> action)
    {
        if (GetBool(state, key))
            actions.Add(action);
    }

    private static string GetStateType(Dictionary<string, object?> state)
    {
        return state.TryGetValue("state_type", out var raw)
            ? (raw?.ToString() ?? string.Empty).Trim().ToLowerInvariant()
            : string.Empty;
    }

    private static int GetFullRunAct(Dictionary<string, object?> state)
    {
        return TryGetDict(state, "run", out var runState) ? GetInt(runState, "act", 0) : 0;
    }

    private static int GetFullRunFloor(Dictionary<string, object?> state)
    {
        return TryGetDict(state, "run", out var runState) ? GetInt(runState, "floor", 0) : 0;
    }

    private static bool IsSettledFullRunState(Dictionary<string, object?> state)
    {
        var stateType = GetStateType(state);
        return stateType is not "" and not "unknown" and not "menu";
    }

    private static bool ShouldReturnImmediatelyForChangedFullRunState(
        Dictionary<string, object?> previousState,
        string previousStateType,
        Dictionary<string, object?> state)
    {
        var stateType = GetStateType(state);
        if (!string.Equals(stateType, previousStateType, StringComparison.Ordinal))
            return true;

        if (GetFullRunAct(state) != GetFullRunAct(previousState) || GetFullRunFloor(state) != GetFullRunFloor(previousState))
            return true;

        return !RequiresStableFullRunPoll(previousStateType) && !RequiresStableFullRunPoll(stateType);
    }

    private static bool RequiresStableFullRunPoll(string stateType)
    {
        return stateType is "monster" or "elite" or "boss" or "hand_select";
    }

    private static bool IsActionableOrTerminalFullRunState(Dictionary<string, object?> state)
    {
        if (IsFullRunTerminalState(state, ExtractFullRunOutcome(state)))
            return true;

        if (!state.TryGetValue("legal_actions", out var rawActions) || rawActions is not IEnumerable enumerable || rawActions is string)
            return false;

        foreach (var item in enumerable)
        {
            if (item is Dictionary<string, object?>)
                return true;
        }

        return false;
    }

    private static string GetFullRunStateSignature(Dictionary<string, object?> state)
    {
        return JsonSerializer.Serialize(state, _jsonOptions);
    }

    private static bool IsFullRunTerminalState(Dictionary<string, object?> state, string? outcome)
    {
        var stateType = GetStateType(state);
        return stateType == "game_over"
            || string.Equals(outcome, "victory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "death", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "win", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "loss", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractFullRunOutcome(Dictionary<string, object?> state)
    {
        if (TryGetString(state, "run_outcome", out var runOutcome))
            return runOutcome;

        if (TryGetDict(state, "game_over", out var gameOverState) && TryGetString(gameOverState, "outcome", out var outcome))
            return outcome;

        return null;
    }

    private static bool IsErrorResult(Dictionary<string, object?> result, out string? error)
    {
        error = null;
        if (result.TryGetValue("status", out var statusRaw)
            && string.Equals(statusRaw?.ToString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            error = result.TryGetValue("error", out var errorRaw) ? errorRaw?.ToString() : "Action failed.";
            return true;
        }

        if (result.TryGetValue("error", out var directError) && directError is string directErrorText && !string.IsNullOrWhiteSpace(directErrorText))
        {
            error = directErrorText;
            return true;
        }

        return false;
    }

    private static bool TryGetDict(Dictionary<string, object?> state, string key, out Dictionary<string, object?> dict)
    {
        if (state.TryGetValue(key, out var raw) && raw is Dictionary<string, object?> typed)
        {
            dict = typed;
            return true;
        }

        dict = null!;
        return false;
    }

    private static IEnumerable<Dictionary<string, object?>> EnumerateDictionaries(object? value)
    {
        if (value is not IEnumerable enumerable || value is string)
            yield break;

        foreach (var item in enumerable)
        {
            if (item is Dictionary<string, object?> typed)
                yield return typed;
        }
    }

    private static bool TryGetString(Dictionary<string, object?> state, string key, out string text)
    {
        if (state.TryGetValue(key, out var raw) && raw != null)
        {
            text = raw.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(text);
        }

        text = string.Empty;
        return false;
    }

    private static string GetString(Dictionary<string, object?> state, string key)
    {
        return TryGetString(state, key, out var text) ? text.Trim().ToLowerInvariant() : string.Empty;
    }

    private static int GetInt(Dictionary<string, object?> state, string key, int defaultValue)
    {
        if (!state.TryGetValue(key, out var raw) || raw == null)
            return defaultValue;
        return raw switch
        {
            int value => value,
            long value => (int)value,
            uint value => (int)value,
            JsonElement elem when elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var value) => value,
            _ when int.TryParse(raw.ToString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static int GetOptionalInt(Dictionary<string, JsonElement> payload, string key, int defaultValue)
    {
        if (payload.TryGetValue(key, out var elem) && elem.ValueKind == JsonValueKind.Number)
            return elem.GetInt32();
        return defaultValue;
    }

    private static bool GetBool(Dictionary<string, object?> state, string key, bool defaultValue = false)
    {
        if (!state.TryGetValue(key, out var raw) || raw == null)
            return defaultValue;
        return raw switch
        {
            bool value => value,
            JsonElement elem when elem.ValueKind == JsonValueKind.True => true,
            JsonElement elem when elem.ValueKind == JsonValueKind.False => false,
            _ when bool.TryParse(raw.ToString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static bool IsFullRunSimulatorActive()
    {
        try { return FullRunSimulationMode.IsServerActive || FullRunSimulationMode.IsSmokeActive; }
        catch { return false; }
    }

    private static FullRunSimulationResetRequest ParseSimResetRequest(Dictionary<string, JsonElement> parsed)
    {
        var req = new FullRunSimulationResetRequest();
        if (parsed.TryGetValue("character_id", out var charElem) && charElem.ValueKind == JsonValueKind.String)
            req.CharacterId = charElem.GetString();
        if (parsed.TryGetValue("seed", out var seedElem) && seedElem.ValueKind == JsonValueKind.String)
            req.Seed = seedElem.GetString();
        if (parsed.TryGetValue("ascension_level", out var ascElem) && ascElem.ValueKind == JsonValueKind.Number)
            req.AscensionLevel = ascElem.GetInt32();
        return req;
    }

    private static FullRunSimulationActionRequest ParseSimActionRequest(Dictionary<string, JsonElement> parsed)
    {
        var req = new FullRunSimulationActionRequest();
        if (parsed.TryGetValue("action", out var actionElem) && actionElem.ValueKind == JsonValueKind.String)
            req.Action = actionElem.GetString() ?? string.Empty;
        if (parsed.TryGetValue("index", out var indexElem) && indexElem.ValueKind == JsonValueKind.Number)
            req.Index = indexElem.GetInt32();
        if (parsed.TryGetValue("card_index", out var cardIndexElem) && cardIndexElem.ValueKind == JsonValueKind.Number)
            req.CardIndex = cardIndexElem.GetInt32();
        if (parsed.TryGetValue("hand_index", out var handIndexElem) && handIndexElem.ValueKind == JsonValueKind.Number)
            req.HandIndex = handIndexElem.GetInt32();
        if (parsed.TryGetValue("slot", out var slotElem) && slotElem.ValueKind == JsonValueKind.Number)
            req.Slot = slotElem.GetInt32();
        if (parsed.TryGetValue("col", out var colElem) && colElem.ValueKind == JsonValueKind.Number)
            req.Col = colElem.GetInt32();
        if (parsed.TryGetValue("row", out var rowElem) && rowElem.ValueKind == JsonValueKind.Number)
            req.Row = rowElem.GetInt32();
        if (parsed.TryGetValue("target", out var targetElem) && targetElem.ValueKind == JsonValueKind.String)
            req.Target = targetElem.GetString();
        if (parsed.TryGetValue("target_id", out var targetIdElem) && targetIdElem.ValueKind == JsonValueKind.Number)
            req.TargetId = targetIdElem.GetUInt32();
        if (parsed.TryGetValue("value", out var valueElem) && valueElem.ValueKind == JsonValueKind.String)
            req.Value = valueElem.GetString();
        return req;
    }
}

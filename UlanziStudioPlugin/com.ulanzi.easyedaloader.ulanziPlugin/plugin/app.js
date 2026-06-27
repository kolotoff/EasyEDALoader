import { UlanziApi } from './actions/ulanzi-api/index.js';
import {
  CommandOpenLoader,
  CommandReproject3D,
  CommandAlign3DModel,
  CommandLayerTop,
  CommandLayerBottom,
  CommandLayerNext,
  CommandLayerPrevious,
  sendEasyEdaCommand
} from './easyedaBridgeClient.js';

const pluginUuid = 'com.ulanzi.ulanzideck.easyedaloader';
const actionIds = {
  Dial: 'com.ulanzi.ulanzideck.easyedaloader.dial',
  OpenLoader: 'com.ulanzi.ulanzideck.easyedaloader.openloader',
  LayerNext: 'com.ulanzi.ulanzideck.easyedaloader.layernext',
  LayerPrevious: 'com.ulanzi.ulanzideck.easyedaloader.layerprevious',
  LayerTop: 'com.ulanzi.ulanzideck.easyedaloader.layertop',
  LayerBottom: 'com.ulanzi.ulanzideck.easyedaloader.layerbottom',
  Reproject3D: 'com.ulanzi.ulanzideck.easyedaloader.reproject3d',
  Align3DModel: 'com.ulanzi.ulanzideck.easyedaloader.align3dmodel'
};
const commandByActionId = new Map([
  [actionIds.OpenLoader, CommandOpenLoader],
  [actionIds.LayerNext, CommandLayerNext],
  [actionIds.LayerPrevious, CommandLayerPrevious],
  [actionIds.LayerTop, CommandLayerTop],
  [actionIds.LayerBottom, CommandLayerBottom],
  [actionIds.Reproject3D, CommandReproject3D],
  [actionIds.Align3DModel, CommandAlign3DModel]
]);
const actionInstances = new Map();
const recentKeyCommands = new Map();
const keyRunDedupeWindowMs = 350;
const $UD = new UlanziApi();

$UD.connect(pluginUuid);

$UD.onAdd((message) => {
  actionInstances.set(message.context, {
    context: message.context,
    actionUuid: resolveActionUuid(message)
  });
});

$UD.onRun((message) => {
  if (wasRecentlyHandledByKeyDown(message)) {
    return;
  }

  runCommandFromEvent(message, null);
});

$UD.onKeyDown((message) => {
  markHandledByKeyDown(message);
  runCommandFromEvent(message, null);
});

$UD.onDialRotateLeft((message) => {
  runCommandFromEvent(message, CommandLayerPrevious);
});

$UD.onDialRotateRight((message) => {
  runCommandFromEvent(message, CommandLayerNext);
});

$UD.onDialRotateHoldLeft((message) => {
  runCommandFromEvent(message, CommandLayerTop);
});

$UD.onDialRotateHoldRight((message) => {
  runCommandFromEvent(message, CommandLayerBottom);
});

$UD.onClear((message) => {
  const cleared = message.param || [];
  for (const item of cleared) {
    if (item.context) {
      actionInstances.delete(item.context);
    }
  }
});

function runCommandFromEvent(message, fallbackCommand) {
  const command = resolveCommand(message, fallbackCommand);
  if (!command) {
    $UD.logMessage('EasyEDALoader command ignored: unresolved Ulanzi action.', 'info');
    return;
  }

  runCommand(command, message?.context);
}

function resolveCommand(message, fallbackCommand) {
  const actionUuid = resolveActionUuid(message);
  return commandByActionId.get(actionUuid) || fallbackCommand;
}

function resolveActionUuid(message) {
  if (message?.uuid && commandByActionId.has(message.uuid)) {
    return message.uuid;
  }

  if (message?.actionid && commandByActionId.has(message.actionid)) {
    return message.actionid;
  }

  if (message?.context) {
    const actionInstance = actionInstances.get(message.context);
    if (actionInstance?.actionUuid) {
      return actionInstance.actionUuid;
    }

    try {
      const decodedContext = $UD.decodeContext(message.context);
      if (decodedContext.uuid && commandByActionId.has(decodedContext.uuid)) {
        return decodedContext.uuid;
      }

      return decodedContext.actionid || '';
    } catch {
      return '';
    }
  }

  return '';
}

function markHandledByKeyDown(message) {
  recentKeyCommands.set(commandEventKey(message), Date.now());
}

function wasRecentlyHandledByKeyDown(message) {
  const eventKey = commandEventKey(message);
  const handledAt = recentKeyCommands.get(eventKey) || 0;
  if (Date.now() - handledAt <= keyRunDedupeWindowMs) {
    return true;
  }

  recentKeyCommands.delete(eventKey);
  return false;
}

function commandEventKey(message) {
  return `${message?.context || ''}:${resolveActionUuid(message)}`;
}

async function runCommand(command, context) {
  try {
    await sendEasyEdaCommand(command);
    $UD.logMessage(`EasyEDALoader command executed: ${command}`, 'info');
  } catch (error) {
    $UD.logMessage(`EasyEDALoader command failed: ${error.message}`, 'error');
    if (context) {
      $UD.showAlert(context);
    }
    $UD.toast(error.message);
  }
}

export {
  CommandOpenLoader,
  CommandReproject3D,
  CommandAlign3DModel,
  CommandLayerTop,
  CommandLayerBottom,
  CommandLayerNext,
  CommandLayerPrevious
};

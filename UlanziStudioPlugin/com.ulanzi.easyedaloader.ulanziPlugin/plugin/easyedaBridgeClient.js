import net from 'net';

export const CommandOpenLoader = 'open-loader';
export const CommandReproject3D = 'reproject-3d';
export const CommandAlign3DModel = 'align-3d-model';
export const CommandLayerTop = 'layer-top';
export const CommandLayerBottom = 'layer-bottom';
export const CommandLayerNext = 'layer-next';
export const CommandLayerPrevious = 'layer-previous';
export const CommandLayerSelectedPrimitive = 'layer-selected-primitive';

const pipePath = '\\\\.\\pipe\\EasyEDA-Loader.CommandBridge';
const retryablePipeErrorCodes = new Set(['ENOENT', 'EBUSY', 'ECONNREFUSED']);
const transientRetryDelayMs = 40;
const transientRetryWindowMs = 1200;
const busyCommandThresholdMs = 1000;
let commandQueue = Promise.resolve();
let activeCommand = '';
let activeCommandStartedAt = 0;

export function sendEasyEdaCommand(command) {
  if (isBridgeCommandBusy()) {
    return Promise.reject(createBusyError());
  }

  const queuedCommand = commandQueue.then(async () => {
    if (isBridgeCommandBusy()) {
      throw createBusyError();
    }

    activeCommand = command;
    activeCommandStartedAt = Date.now();
    try {
      return await connectWithRetry(command);
    } finally {
      activeCommand = '';
      activeCommandStartedAt = 0;
    }
  });
  commandQueue = queuedCommand.catch(() => {});
  return queuedCommand;
}

function isBridgeCommandBusy() {
  if (!activeCommand) {
    return false;
  }

  if (activeCommand === CommandOpenLoader) {
    return true;
  }

  return activeCommandStartedAt > 0 && Date.now() - activeCommandStartedAt >= busyCommandThresholdMs;
}

function createBusyError() {
  return new Error('EasyEDALoader is busy. Close the EasyEDALoader window or wait for the current command to finish.');
}

async function connectWithRetry(command) {
  const startedAt = Date.now();
  let lastError = null;

  do {
    try {
      return await connectOnce(command);
    } catch (error) {
      lastError = error;
      if (!isTransientPipeError(error) || Date.now() - startedAt >= transientRetryWindowMs) {
        break;
      }

      await delay(transientRetryDelayMs);
    }
  } while (true);

  if (lastError && lastError.code === 'ENOENT') {
    throw new Error('EasyEDALoader bridge pipe was not found. Reinstall or restart the EasyEDALoader Altium extension, then keep Altium running with an Altium window active.');
  }

  throw lastError;
}

function connectOnce(command) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(pipePath);
    let response = '';
    let settled = false;

    const finishResolve = (value) => {
      if (settled) {
        return;
      }

      settled = true;
      resolve(value);
    };

    const finishReject = (error) => {
      if (settled) {
        return;
      }

      settled = true;
      reject(error);
    };

    socket.setEncoding('utf8');
    socket.setTimeout(5000);

    socket.on('connect', () => {
      socket.write(JSON.stringify({ command }) + '\n');
    });

    socket.on('data', (chunk) => {
      response += chunk;
      if (response.includes('\n')) {
        socket.end();
      }
    });

    socket.on('timeout', () => {
      finishReject(new Error('Timed out waiting for EasyEDALoader bridge response.'));
      socket.destroy();
    });

    socket.on('error', (error) => {
      finishReject(error);
    });

    socket.on('close', () => {
      if (settled) {
        return;
      }

      if (!response.trim()) {
        finishReject(new Error('EasyEDALoader bridge returned no response. Confirm Altium is running and EasyEDALoader is installed.'));
        return;
      }

      try {
        const parsed = JSON.parse(response.trim());
        if (!parsed.success) {
          finishReject(new Error(parsed.message || parsed.errorCode || 'EasyEDALoader command failed.'));
          return;
        }

        finishResolve(parsed);
      } catch (error) {
        finishReject(error);
      }
    });
  });
}

function isTransientPipeError(error) {
  return error && retryablePipeErrorCodes.has(error.code);
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

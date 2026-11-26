// The VS Code host test's stand-in for the C# extension's `coreclr` debugger (design/ide-run-debug §4), so CI needs no
// C# extension: an inline debug adapter that records each `launch` request's arguments and then ends the session the
// way the scenario M app ends, exit code 2 when a Debug build starts without /mock and 0 with it. Nothing is run.
const vscode = require('vscode');

/** The `launch` requests, oldest first; the host test reads them through this extension's exports. */
const launches = [];

class StandInAdapter {
  constructor() { this.emitter = new vscode.EventEmitter(); this.onDidSendMessage = this.emitter.event; this.seq = 1; }
  send(message) { this.emitter.fire({ seq: this.seq++, ...message }); }
  respond(request, body = {}) { this.send({ type: 'response', request_seq: request.seq, success: true, command: request.command, body }); }
  event(event, body = {}) { this.send({ type: 'event', event, body }); }
  handleMessage(message) {
    if (message.type !== 'request') return;
    switch (message.command) {
      case 'initialize': this.respond(message, { supportsConfigurationDoneRequest: true }); this.event('initialized'); break;
      case 'launch': {
        const args = message.arguments ?? {};
        launches.push(JSON.parse(JSON.stringify(args)));
        this.respond(message);
        const mock = Array.isArray(args.args) && args.args.includes('/mock');
        this.event('output', { category: 'stdout', output: mock ? `Acme.Mock: mock mode, arguments: [${args.args.join(', ')}]\n` : `Acme.Mock: a Debug build starts only with /mock; arguments: [${(args.args ?? []).join(', ')}]\n` });
        setTimeout(() => { this.event('exited', { exitCode: mock ? 0 : 2 }); this.event('terminated'); }, 50);
        break;
      }
      default: this.respond(message);
    }
  }
  dispose() { this.emitter.dispose(); }
}

exports.activate = function (context) {
  context.subscriptions.push(vscode.debug.registerDebugAdapterDescriptorFactory('coreclr', { createDebugAdapterDescriptor: () => new vscode.DebugAdapterInlineImplementation(new StandInAdapter()) }));
  return { launches };
};

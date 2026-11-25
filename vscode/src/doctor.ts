/** `dotnet runic doctor --format json` (`runic.application.tool.doctor/1`, tools/dotnet-runic/README.md "Doctor JSON output"). */
export interface DoctorCheck { id: string; status: "pass" | "warn" | "fail"; message: string; remediation: string | null }
export interface DoctorReport {
  project: string; host: string; target: string | null; healthy: boolean;
  summary: { passed: number; warnings: number; failed: number }; checks: DoctorCheck[]; hosts?: string[];
}
export const doctorPayloadType = "runic.application.tool.doctor/1";

/** `--fail-on never` reports every completed inspection as a successful envelope, so the checks are always in `payload`. */
export function doctorArguments(project: string, verb = "runic"): string[] {
  return [verb, "doctor", "--project", project, "--format", "json", "--fail-on", "never"];
}

interface Envelope {
  protocol?: string; success?: boolean; payloadType?: string; payload?: unknown;
  fault?: { code?: string; message?: string; helpUri?: string; data?: { type?: string; payload?: unknown } } | null;
}

/**
 * The doctor report of a `runic.commandline/1` envelope: its payload, or the payload of a failed envelope's
 * `fault.data` (`--fail-on fail|warn`). A fault without a report (a missing project, an unrestored tool) throws its
 * code and message.
 */
export function parseDoctor(stdout: string): DoctorReport {
  let envelope: Envelope;
  try { envelope = JSON.parse(stdout) as Envelope; }
  catch { throw new Error(`dotnet runic doctor did not write a JSON envelope: ${stdout.trim().split("\n").at(-1) ?? ""}`); }
  const report = envelope.payloadType === doctorPayloadType ? envelope.payload : envelope.fault?.data?.type === doctorPayloadType ? envelope.fault.data.payload : undefined;
  if (report && typeof report === "object" && Array.isArray((report as DoctorReport).checks)) return report as DoctorReport;
  const fault = envelope.fault;
  throw new Error(fault ? `${fault.code ? `${fault.code}: ` : ""}${fault.message ?? "dotnet runic doctor failed."}` : "dotnet runic doctor returned no report.");
}

/** Checks in the order the Doctor view lists them: failures, then warnings, then passing checks, each in the tool's order. */
export function orderedChecks(report: DoctorReport): DoctorCheck[] {
  const rank = { fail: 0, warn: 1, pass: 2 } as const;
  return report.checks.map((check, index) => ({ check, index })).sort((a, b) => (rank[a.check.status] ?? 3) - (rank[b.check.status] ?? 3) || a.index - b.index).map(entry => entry.check);
}

export function doctorSummary(report: DoctorReport): string {
  const { passed, warnings, failed } = report.summary;
  return `${failed} failed, ${warnings} warning${warnings === 1 ? "" : "s"}, ${passed} passed`;
}

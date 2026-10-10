import fs from "node:fs";
import { createBrowser } from "./helpers.mjs";
import { runBoostingMessagesTests } from "./tests/boosting_messages.test.mjs";
import { runComposerTests } from "./tests/composer.test.mjs";
import { runSendingMessagesTests } from "./tests/sending_messages.test.mjs";
import { runUnfurlingLinksTests } from "./tests/unfurling_links.test.mjs";
import { runUnreadRoomsTests } from "./tests/unread_rooms.test.mjs";

function parseArgs() {
  const args = process.argv.slice(2);
  const options = {
    server: "http://127.0.0.1:3200",
    suite: null,
    report: null,
  };

  for (let i = 0; i < args.length; i++) {
    if (args[i] === "--server" && i + 1 < args.length) {
      options.server = args[++i];
    } else if (args[i] === "--suite" && i + 1 < args.length) {
      options.suite = args[++i];
    } else if (args[i] === "--report" && i + 1 < args.length) {
      options.report = args[++i];
    } else if (!args[i].startsWith("--") && !options.serverSet) {
      options.server = args[i];
      options.serverSet = true;
    }
  }

  return options;
}

export async function runWorkflowsAgainstServer(serverUrl, options = {}) {
  const browser = await createBrowser();
  const allResults = [];

  const suites = [
    { name: "boosting_messages", fn: runBoostingMessagesTests },
    { name: "composer", fn: runComposerTests },
    { name: "sending_messages", fn: runSendingMessagesTests },
    { name: "unfurling_links", fn: runUnfurlingLinksTests },
    { name: "unread_rooms", fn: runUnreadRoomsTests },
  ];

  try {
    for (const suite of suites) {
      if (options.suite && options.suite !== suite.name) continue;
      console.log(`\nRunning suite: ${suite.name} against ${serverUrl}`);
      const results = await suite.fn(browser, serverUrl, (r) => {
        if (r.passed) {
          console.log(`  ✔ ${r.name} (${r.duration}ms)`);
        } else {
          console.error(`  ✘ ${r.name} (${r.duration}ms)`);
          console.error(`    Error: ${r.error}`);
          if (r.stack) console.error(`    ${r.stack.split("\n").slice(1, 3).join("\n    ")}`);
        }
      });
      allResults.push(...results.map((r) => ({ ...r, server: serverUrl })));
    }
  } finally {
    await browser.close();
  }

  return allResults;
}

async function main() {
  const options = parseArgs();
  console.log(`Starting Playwright workflows against ${options.server}...`);

  const results = await runWorkflowsAgainstServer(options.server, options);

  const total = results.length;
  const passed = results.filter((r) => r.passed).length;
  const failed = results.filter((r) => !r.passed).length;

  console.log(`\nResults for ${options.server}: ${passed}/${total} passed, ${failed} failed`);

  if (options.report) {
    fs.writeFileSync(options.report, JSON.stringify({ results, total, passed, failed }, null, 2));
    console.log(`Report written to ${options.report}`);
  }

  process.exit(failed > 0 ? 1 : 0);
}

if (process.argv[1] && process.argv[1].endsWith("runner.mjs")) {
  main().catch((err) => {
    console.error("Fatal error:", err);
    process.exit(2);
  });
}

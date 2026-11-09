import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
	testDir: "./e2e",
	fullyParallel: false,
	workers: 1,
	use: { baseURL: process.env["E2E_URL"] ?? "http://localhost:4200", screenshot: "only-on-failure" },
	projects: [
		{ name: "desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 1000 } } },
		{ name: "mobile", use: { ...devices["Pixel 7"] } }
	]
});
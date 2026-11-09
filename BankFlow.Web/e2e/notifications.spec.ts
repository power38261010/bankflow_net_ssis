import { expect, test, request as playwrightRequest } from "@playwright/test";

const isInboxRead = (url: string) => {
	const parsed = new URL(url);
	return parsed.pathname === "/api/notifications" && parsed.searchParams.get("page") === "1";
};

test("delivers a private transfer notification over SignalR and retains it in the inbox", async ({ page, request }) => {
	test.skip(!process.env["E2E_EMAIL"] || !process.env["E2E_PASSWORD"], "Set test-only operator credentials.");
	test.setTimeout(60000);
	await expect(async () => expect((await request.get("/api/auth/csrf")).status()).toBe(204)).toPass({ timeout: 30000 });
	const cookies = (await request.storageState()).cookies;
	const xsrf = decodeURIComponent(cookies.find(cookie => cookie.name === "XSRF-TOKEN")!.value);
	const headers = { "X-XSRF-TOKEN": xsrf };
	expect((await request.post("/api/auth/login", { headers, data: { username: process.env["E2E_EMAIL"], password: process.env["E2E_PASSWORD"] } })).status()).toBe(204);
	await request.get("/api/auth/csrf");
	const operatorCookies = (await request.storageState()).cookies;
	const operator = await playwrightRequest.newContext({ baseURL: "http://127.0.0.1:4200", extraHTTPHeaders: { Cookie: operatorCookies.map(cookie => `${cookie.name}=${encodeURIComponent(cookie.value)}`).join("; ") } });
	const operatorCsrf = decodeURIComponent(operatorCookies.find(cookie => cookie.name === "XSRF-TOKEN")!.value);
	const prefix = `N${Date.now()}`; const password = "TestingOnly123!";
	const receiver = await operator.post("/api/clients", { headers: { "X-XSRF-TOKEN": operatorCsrf }, data: { email: `${prefix.toLowerCase()}@bankflow.test`, password } });
	const receiverId = (await receiver.json()).id;
	const sourceOwner = await operator.post("/api/clients", { headers: { "X-XSRF-TOKEN": operatorCsrf }, data: { email: `${prefix.toLowerCase()}-s@bankflow.test`, password } });
	const sourceOwnerId = (await sourceOwner.json()).id;
	const source = await operator.post("/api/accounts", { headers: { "X-XSRF-TOKEN": operatorCsrf }, data: { customerName: "Source", accountNumber: `${prefix}01`, ownerUserId: sourceOwnerId } });
	const destination = await operator.post("/api/accounts", { headers: { "X-XSRF-TOKEN": operatorCsrf }, data: { customerName: "Receiver", accountNumber: `${prefix}02`, ownerUserId: receiverId } });
	const sourceId = (await source.json()).id;
	expect((await operator.post(`/api/accounts/${sourceId}/deposit`, { headers: { "X-XSRF-TOKEN": operatorCsrf }, data: { amount: 50 } })).status()).toBe(200);
	const initialInbox = page.waitForResponse(response => isInboxRead(response.url()) && response.request().method() === "GET");
	await page.goto("/login");
	await page.getByLabel("Correo electronico").fill(`${prefix.toLowerCase()}@bankflow.test`);
	await page.getByLabel("Contrasena", { exact: true }).fill(password); await page.getByRole("button", { name: "Iniciar sesion", exact: true }).click();
	await expect(page.getByRole("heading", { name: "Mis cuentas" })).toBeVisible();
	expect((await initialInbox).status()).toBe(200);
	const delivered = page.waitForRequest(request => isInboxRead(request.url()) && request.method() === "GET");
	const pageStart = Date.now();
	const transfer = await operator.post("/api/transfers", { headers: { "X-XSRF-TOKEN": operatorCsrf, "Idempotency-Key": crypto.randomUUID() },
		data: { sourceAccountId: sourceId, destinationAccountNumber: `${prefix}02`, amount: 10, description: "Realtime notification" } });
	expect(transfer.status()).toBe(201);
	await delivered;
	expect(Date.now() - pageStart).toBeLessThan(10000);
	await expect(page.getByRole("button", { name: "Notificaciones" })).toHaveAttribute("aria-label", "Notificaciones");
	await expect(page.locator(".mat-badge-content")).toHaveText("1");
	await page.getByRole("button", { name: "Notificaciones" }).click();
	const received = page.getByRole("menuitem").filter({ hasText: "Transferencia recibida" });
	await expect(received).toContainText("Transferencia recibida");
	await expect(received).toContainText(`cuenta •${`${prefix}01`.slice(-4)}`);
	await expect(received).not.toContainText(`${prefix}01`);
	expect((await (await page.request.get("/api/notifications")).json()).totalCount).toBe(1);
	const markRead = page.waitForResponse(response => response.url().endsWith("/api/notifications/read") && response.request().method() === "POST");
	await page.getByRole("menuitem").filter({ hasText: "Transferencia recibida" }).click();
	expect((await markRead).status()).toBe(204);
	expect((await (await page.request.get("/api/notifications")).json()).unreadCount).toBe(0);
	await page.reload(); await page.getByRole("button", { name: "Notificaciones" }).click();
	await expect(page.getByRole("menuitem").filter({ hasText: "Transferencia recibida" })).toBeVisible();
	await operator.dispose();
});
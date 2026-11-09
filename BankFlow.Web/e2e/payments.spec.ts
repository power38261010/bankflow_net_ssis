import { expect, test } from "@playwright/test";

test("saves a beneficiary, executes one scheduled payment and cancels another", async ({ page, request }, testInfo) => {
	test.skip(!process.env["E2E_EMAIL"] || !process.env["E2E_PASSWORD"], "Set test-only operator credentials.");
	test.setTimeout(90000);
	const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
	await expect(async () => expect((await request.get("/api/auth/csrf")).status()).toBe(204)).toPass({ timeout: 30000 });
	let token = decodeURIComponent((await request.storageState()).cookies.find(cookie => cookie.name === "XSRF-TOKEN")!.value);
	expect((await request.post("/api/auth/login", { headers: { "X-XSRF-TOKEN": token }, data: {
		username: process.env["E2E_EMAIL"], password: process.env["E2E_PASSWORD"]
	} })).status()).toBe(204);
	await request.get("/api/auth/csrf"); token = decodeURIComponent((await request.storageState()).cookies.find(cookie => cookie.name === "XSRF-TOKEN")!.value);
	const prefix = `S${Date.now()}`; const email = `${prefix.toLowerCase()}@bankflow.test`; const password = "TestingOnly123!";
	const client = await request.post("/api/clients", { headers: { "X-XSRF-TOKEN": token }, data: { email, password } }); expect(client.status()).toBe(201);
	const owner = (await client.json()).id;
	const source = await request.post("/api/accounts", { headers: { "X-XSRF-TOKEN": token }, data: { customerName: "Scheduled sender", accountNumber: prefix + "01", ownerUserId: owner } }); expect(source.status()).toBe(201);
	const sourceId = (await source.json()).id;
	const destination = await request.post("/api/accounts", { headers: { "X-XSRF-TOKEN": token }, data: { customerName: "Scheduled receiver", accountNumber: prefix + "02", ownerUserId: owner } }); expect(destination.status()).toBe(201);
	expect((await request.post(`/api/accounts/${sourceId}/deposit`, { headers: { "X-XSRF-TOKEN": token }, data: { amount: 100 } })).status()).toBe(200);
	await page.goto("/login"); await page.getByLabel("Correo electronico").fill(email); await page.getByLabel("Contrasena", { exact: true }).fill(password);
	await page.getByRole("button", { name: "Iniciar sesion", exact: true }).click(); await expect(page.getByRole("heading", { name: "Mis cuentas" })).toBeVisible();
	await page.getByRole("link", { name: "Pagos", exact: true }).click(); await expect(page.getByRole("heading", { name: "Pagos programados" })).toBeVisible();
	await page.getByRole("button", { name: "Nuevo beneficiario", exact: true }).click();
	await page.getByLabel("Nombre del beneficiario").fill("Beneficiario de prueba"); await page.getByLabel("Cuenta destino", { exact: true }).fill(prefix + "02");
	await page.getByRole("button", { name: "Confirmar", exact: true }).click(); await expect(page.getByRole("dialog")).not.toBeVisible();
	await expect(page.locator(".beneficiary-row")).toHaveCount(1);
	let creationKey = "";
	await page.route("**/api/scheduled-payments", async route => {
		if (route.request().method() !== "POST") { await route.continue(); return; }
		creationKey = route.request().headers()["idempotency-key"];
		const response = await route.fetch(); expect(response.status()).toBe(201); await route.abort("failed");
	}, { times: 1 });
	const create = async (future: number) => {
		await page.getByRole("button", { name: "Programar pago", exact: true }).click();
		await page.getByRole("combobox", { name: "Cuenta origen", exact: true }).click(); await page.getByRole("option").filter({ hasText: prefix + "01" }).click();
		await page.getByRole("combobox", { name: "Beneficiario", exact: true }).click(); await page.getByRole("option").filter({ hasText: "Beneficiario de prueba" }).click();
		await page.getByLabel("Importe (EUR)", { exact: true }).fill("25");
		const due = new Date(Date.now() + future);
		const local = `${due.getFullYear()}-${String(due.getMonth() + 1).padStart(2, "0")}-${String(due.getDate()).padStart(2, "0")}T${String(due.getHours()).padStart(2, "0")}:${String(due.getMinutes()).padStart(2, "0")}:${String(due.getSeconds()).padStart(2, "0")}`;
		await page.getByLabel("Fecha y hora local", { exact: true }).fill(local);
		await page.getByLabel("Concepto", { exact: true }).fill("Pago de prueba");
		const response = page.waitForResponse(response => response.url().endsWith("/api/scheduled-payments") && response.request().method() === "POST");
		await page.getByRole("button", { name: "Confirmar", exact: true }).click(); const result = await response; expect(result.status()).toBe(201);
		await expect(page.getByRole("dialog")).not.toBeVisible(); return (await result.json()).id as string;
	};
	await page.getByRole("button", { name: "Programar pago", exact: true }).click();
	await page.getByRole("combobox", { name: "Cuenta origen", exact: true }).click(); await page.getByRole("option").filter({ hasText: prefix + "01" }).click();
	await page.getByRole("combobox", { name: "Beneficiario", exact: true }).click(); await page.getByRole("option").filter({ hasText: "Beneficiario de prueba" }).click();
	await page.getByLabel("Importe (EUR)", { exact: true }).fill("25");
	const date = new Date(Date.now() + 10000);
	const localDate = new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 19);
	await page.getByLabel("Fecha y hora local", { exact: true }).fill(localDate);
	await page.getByRole("button", { name: "Confirmar", exact: true }).click();
	await expect(page.getByRole("alert")).toContainText("No se pudo confirmar la programacion");
	await page.getByRole("dialog").getByRole("button", { name: "Cerrar", exact: true }).click();
	await page.reload();
	await expect(page.getByRole("heading", { name: "Pagos programados", exact: true })).toBeVisible();
	await page.getByRole("button", { name: "Programar pago", exact: true }).click();
	const replay = page.waitForResponse(response => response.url().endsWith("/api/scheduled-payments") && response.request().method() === "POST");
	await page.getByRole("button", { name: "Reintentar programacion", exact: true }).click();
	const repeated = await replay; expect(repeated.status()).toBe(201); expect(repeated.request().headers()["idempotency-key"]).toBe(creationKey);
	const id = (await repeated.json()).id;
	await expect(page.getByRole("dialog")).not.toBeVisible();
	await expect.poll(async () => (await (await page.request.get(`/api/scheduled-payments/${id}`)).json()).status, { timeout: 40000, intervals: [1000] }).toBe(2);
	expect((await (await page.request.get(`/api/accounts/${sourceId}`)).json()).balance).toBe(75);
	await page.getByRole("button", { name: "Actualizar pagos", exact: true }).click(); await expect(page.getByText("Completado", { exact: true })).toBeVisible();
	const cancelled = await create(3600000);
	await page.getByRole("button", { name: "Cancelar pago a Beneficiario de prueba", exact: true }).click();
	await page.getByRole("button", { name: "Confirmar", exact: true }).click(); await expect(page.getByRole("dialog")).not.toBeVisible();
	expect((await (await page.request.get(`/api/scheduled-payments/${cancelled}`)).json()).status).toBe(3);
	await page.getByRole("button", { name: "Eliminar beneficiario Beneficiario de prueba", exact: true }).click(); await page.getByRole("button", { name: "Confirmar", exact: true }).click();
	await expect(page.getByRole("dialog")).not.toBeVisible(); await expect(page.locator(".beneficiary-row")).toHaveCount(0);
	expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
	await page.screenshot({ path: testInfo.outputPath("scheduled-payments.png"), fullPage: true, animations: "disabled" }); expect(errors).toEqual([]);
});
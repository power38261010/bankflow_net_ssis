import { expect, test } from "@playwright/test";
import { TOTP, Secret } from "otpauth";

test.beforeAll(async ({ request }) => {
	await expect(async () => {
		const response = await request.get("/api/auth/csrf");
		expect(response.status()).toBe(204);
	}).toPass({ timeout: 30000 });
});

test("paginates accounts and movements while searching on the server", async ({ page, request }, testInfo) => {
	test.skip(!process.env["E2E_EMAIL"] || !process.env["E2E_PASSWORD"], "Set test-only operator credentials.");
	test.setTimeout(60000);
	await request.get("/api/auth/csrf");
	let token = decodeURIComponent((await request.storageState()).cookies.find(cookie => cookie.name === "XSRF-TOKEN")!.value);
	expect((await request.post("/api/auth/login", { headers: { "X-XSRF-TOKEN": token }, data: {
		username: process.env["E2E_EMAIL"], password: process.env["E2E_PASSWORD"]
	} })).status()).toBe(204);
	await request.get("/api/auth/csrf");
	token = decodeURIComponent((await request.storageState()).cookies.find(cookie => cookie.name === "XSRF-TOKEN")!.value);
	const prefix = `P${Date.now()}`;
	const ownerResponse = await request.post("/api/clients", { headers: { "X-XSRF-TOKEN": token },
		data: { email: `${prefix.toLowerCase()}@bankflow.test`, password: "TestingOnly123!" } });
	expect(ownerResponse.status()).toBe(201);
	const owner = (await ownerResponse.json()).id;
	const accounts: { id: string; number: string }[] = [];
	for (let index = 0; index < 23; index++) {
		const number = `${prefix}${index.toString().padStart(2, "0")}`;
		const response = await request.post("/api/accounts", { headers: { "X-XSRF-TOKEN": token },
			data: { customerName: "Pagination fixture", accountNumber: number, ownerUserId: owner } });
		expect(response.status()).toBe(201); accounts.push({ id: (await response.json()).id, number });
	}
	for (let index = 0; index < 23; index++)
		expect((await request.post(`/api/accounts/${accounts[0].id}/deposit`, {
			headers: { "X-XSRF-TOKEN": token }, data: { amount: 1 }
		})).status()).toBe(200);
	await page.goto("/login");
	await page.getByLabel("Correo electronico").fill(process.env["E2E_EMAIL"]!);
	await page.getByLabel("Contrasena", { exact: true }).fill(process.env["E2E_PASSWORD"]!);
	await page.getByRole("button", { name: "Iniciar sesion", exact: true }).click();
	await expect(page.getByRole("heading", { name: "Cuentas bancarias" })).toBeVisible();
	await expect(page.locator("tbody tr")).toHaveCount(20);
	const globalBalance = await page.locator(".metrics .metric").first().locator("strong").innerText();
	await page.getByLabel("Buscar titular o cuenta").fill(prefix);
	await expect(page.locator("tbody tr")).toHaveCount(20);
	const paginator = page.getByLabel("Paginas de cuentas", { exact: true });
	await expect(paginator).toContainText("de 23");
	const firstPage = await page.locator("tbody .account-number").allTextContents();
	await paginator.getByRole("button", { name: "Pagina siguiente", exact: true }).click();
	await expect(page.locator("tbody tr")).toHaveCount(3);
	const secondPage = await page.locator("tbody .account-number").allTextContents();
	expect(secondPage.every(number => !firstPage.includes(number))).toBe(true);
	await expect(page.locator(".metrics .metric").first().locator("strong")).toHaveText(globalBalance);
	await page.getByLabel("Buscar titular o cuenta").fill(accounts[0].number);
	await expect(page.locator("tbody tr")).toHaveCount(1);
	await page.getByRole("button", { name: `Historial de ${accounts[0].number}` }).click();
	await expect(page.locator(".transaction")).toHaveCount(20);
	const history = page.getByLabel("Paginas de movimientos", { exact: true });
	await expect(history).toContainText("de 23");
	await history.getByRole("button", { name: "Pagina siguiente", exact: true }).click();
	await expect(page.locator(".transaction")).toHaveCount(3);
	expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
	await page.screenshot({ path: testInfo.outputPath("pagination.png"), fullPage: true, animations: "disabled" });
});

test("enrolls MFA, protects login and manages one-use recovery codes", async ({ page, request, context }, testInfo) => {
	test.skip(!process.env["E2E_EMAIL"] || !process.env["E2E_PASSWORD"], "Set test-only operator credentials.");
	test.setTimeout(60000);
	const errors: string[] = [];
	page.on("pageerror", error => errors.push(error.message));
	await request.get("/api/auth/csrf");
	let token = decodeURIComponent((await request.storageState()).cookies.find(cookie => cookie.name === "XSRF-TOKEN")!.value);
	expect((await request.post("/api/auth/login", { headers: { "X-XSRF-TOKEN": token },
		data: { username: process.env["E2E_EMAIL"], password: process.env["E2E_PASSWORD"] } })).status()).toBe(204);
	await request.get("/api/auth/csrf");
	token = decodeURIComponent((await request.storageState()).cookies.find(cookie => cookie.name === "XSRF-TOKEN")!.value);
	const email = `mfa-${Date.now()}@bankflow.test`;
	const password = "TestingOnly123!";
	expect((await request.post("/api/clients", { headers: { "X-XSRF-TOKEN": token }, data: { email, password } })).status()).toBe(201);
	const login = async () => {
		if (!page.url().endsWith("/login")) await page.goto("/login");
		await page.getByLabel("Correo electronico").fill(email);
		await page.getByLabel("Contrasena", { exact: true }).fill(password);
		await page.getByRole("button", { name: "Iniciar sesion", exact: true }).click();
	};
	await login();
	await expect(page.getByRole("heading", { name: "Mis cuentas", exact: true })).toBeVisible();
	await page.getByRole("button", { name: "Seguridad de acceso" }).click();
	await page.getByRole("button", { name: "Activar MFA", exact: true }).click();
	await page.getByLabel("Contrasena actual", { exact: true }).fill(password);
	const setupResponse = page.waitForResponse(response => response.url().endsWith("/api/auth/mfa/setup"));
	await page.getByRole("button", { name: "Generar QR", exact: true }).click();
	const setup = await (await setupResponse).json();
	await expect(page.getByAltText("QR del autenticador")).toBeVisible();
	const pixels = await page.getByAltText("QR del autenticador").evaluate(element => {
		const image = element as HTMLImageElement;
		const canvas = document.createElement("canvas"); canvas.width = 192; canvas.height = 192;
		const drawing = canvas.getContext("2d")!; drawing.drawImage(image, 0, 0, 192, 192);
		const pixels = drawing.getImageData(0, 0, 192, 192).data;
		let dark = 0;
		for (let offset = 0; offset < pixels.length; offset += 4) if (pixels[offset] < 100) dark++;
		return dark;
	});
	expect(pixels).toBeGreaterThan(1000); expect(pixels).toBeLessThan(192 * 192 - 1000);
	const totp = new TOTP({ secret: Secret.fromBase32(setup.sharedKey), digits: 6, period: 30 });
	const used = new Set<string>();
	const nextCode = () => {
		for (const step of [0, 1, 2, -1, -2]) {
			const code = totp.generate({ timestamp: Date.now() + step * 30000 });
			if (!used.has(code)) { used.add(code); return code; }
		}
		throw new Error("No unused TOTP test code is available.");
	};
	const enrollmentCode = nextCode();
	await page.getByLabel("Codigo de autenticador", { exact: true }).fill(enrollmentCode);
	const enableResponse = page.waitForResponse(response => response.url().endsWith("/api/auth/mfa/enable"));
	await page.getByRole("button", { name: "Confirmar activacion", exact: true }).click();
	const recovery = (await (await enableResponse).json()).recoveryCodes as string[];
	expect(recovery).toHaveLength(10);
	await expect(page.locator(".recovery-grid code")).toHaveCount(10);
	await expect(page.getByRole("button", { name: "Finalizar", exact: true })).toBeDisabled();
	await page.getByLabel("He guardado los codigos").check();
	await page.getByRole("button", { name: "Finalizar", exact: true }).click();
	await expect(page.getByText("MFA activado", { exact: true })).toBeVisible();
	await page.getByRole("dialog").getByRole("button", { name: "Cerrar", exact: true }).click();
	await page.getByRole("button", { name: "Cerrar sesion", exact: true }).click();
	await login();
	await expect(page.getByRole("heading", { name: "Verifica tu acceso.", exact: true })).toBeVisible();
	expect((await page.request.get("/api/accounts")).status()).toBe(401);
	const pending = (await context.cookies()).find(cookie => cookie.name === "BankFlow.MfaPending")!;
	expect(pending.httpOnly).toBe(true); expect(pending.sameSite).toBe("Strict");
	await page.reload();
	await expect(page.getByRole("heading", { name: "Verifica tu acceso.", exact: true })).toBeVisible();
	await page.getByLabel("Codigo de autenticador", { exact: true }).fill(enrollmentCode);
	await page.getByRole("button", { name: "Verificar codigo", exact: true }).click();
	await expect(page.getByRole("alert")).toContainText("Codigo incorrecto, usado o caducado");
	expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
	await page.screenshot({ path: testInfo.outputPath("mfa-login.png"), fullPage: true, animations: "disabled" });
	await page.getByLabel("Codigo de autenticador", { exact: true }).fill(nextCode());
	await page.getByRole("button", { name: "Verificar codigo", exact: true }).click();
	await expect(page.getByRole("heading", { name: "Mis cuentas", exact: true })).toBeVisible();
	await page.getByRole("button", { name: "Cerrar sesion", exact: true }).click();
	await login();
	await expect(page.getByRole("heading", { name: "Verifica tu acceso." })).toBeVisible();
	await page.getByLabel("Usar codigo de recuperacion").check();
	await page.getByLabel("Codigo de recuperacion", { exact: true }).fill(recovery[0]);
	await page.getByRole("button", { name: "Verificar codigo", exact: true }).click();
	await expect(page.getByRole("heading", { name: "Mis cuentas", exact: true })).toBeVisible();
	await page.getByRole("button", { name: "Seguridad de acceso" }).click();
	await page.getByRole("button", { name: "Renovar codigos", exact: true }).click();
	await page.getByLabel("Contrasena actual", { exact: true }).fill(password);
	await page.getByLabel("Usar codigo de recuperacion").check();
	await page.getByLabel("Codigo de recuperacion", { exact: true }).fill(recovery[1]);
	const regeneration = page.waitForResponse(response => response.url().endsWith("/api/auth/mfa/recovery-codes"));
	await page.getByRole("button", { name: "Confirmar", exact: true }).click();
	const newRecovery = (await (await regeneration).json()).recoveryCodes as string[];
	await page.getByLabel("He guardado los codigos").check();
	await page.getByRole("button", { name: "Finalizar", exact: true }).click();
	await page.getByRole("button", { name: "Desactivar MFA", exact: true }).click();
	await page.getByLabel("Contrasena actual", { exact: true }).fill(password);
	await page.getByLabel("Usar codigo de recuperacion").check();
	await page.getByLabel("Codigo de recuperacion", { exact: true }).fill(newRecovery[0]);
	await page.getByRole("button", { name: "Confirmar", exact: true }).click();
	await expect(page).toHaveURL(/\/login$/);
	await login();
	await expect(page.getByRole("heading", { name: "Mis cuentas", exact: true })).toBeVisible();
	expect(errors).toEqual([]);
});

test("protects the workspace and validates login fields", async ({ page }, testInfo) => {
	const errors: string[] = [];
	page.on("pageerror", error => errors.push(error.message));
	await page.goto("/");
	await expect(page).toHaveURL(/\/login$/);
	await expect(page.getByRole("heading", { name: "Tu espacio de trabajo." })).toBeVisible();
	await page.evaluate(() => document.fonts.ready);
	expect(await page.locator(".brand mat-icon").evaluate(icon => getComputedStyle(icon).fontFamily)).toContain("Material Icons");
	await page.getByRole("button", { name: "Iniciar sesion" }).click();
	await expect(page.getByText("Introduce un correo valido.")).toBeVisible();
	await page.getByLabel("Correo electronico").fill("unknown@bankflow.test");
	await page.getByLabel("Contrasena", { exact: true }).fill("not-a-real-password");
	await page.getByRole("button", { name: "Mostrar contrasena", exact: true }).click();
	await expect(page.getByLabel("Contrasena", { exact: true })).toHaveAttribute("type", "text");
	await page.getByRole("button", { name: "Ocultar contrasena", exact: true }).click();
	await expect(page.getByLabel("Contrasena", { exact: true })).toHaveAttribute("type", "password");
	await page.getByRole("button", { name: "Iniciar sesion" }).click();
	await expect(page.getByRole("alert")).toContainText("Credenciales incorrectas");
	await expect(page.locator(".login-photo img")).toBeVisible();
	await expect.poll(() => page.locator(".login-photo img").evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
	expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
	await page.screenshot({ path: testInfo.outputPath("login.png"), fullPage: true, animations: "disabled" });
	expect(errors).toEqual([]);
});

test("restores a real session and manages an account", async ({ page, context }, testInfo) => {
	test.skip(!process.env["E2E_EMAIL"] || !process.env["E2E_PASSWORD"], "Set test-only E2E_EMAIL and E2E_PASSWORD.");
	const errors: string[] = [];
	page.on("pageerror", error => errors.push(error.message));
	await page.goto("/login");
	await page.getByLabel("Correo electronico").fill(process.env["E2E_EMAIL"]!);
	await page.getByLabel("Contrasena", { exact: true }).fill(process.env["E2E_PASSWORD"]!);
	await page.getByRole("button", { name: "Iniciar sesion" }).click();
	await expect(page.getByRole("heading", { name: "Cuentas bancarias" })).toBeVisible();
	const cookie = (await context.cookies()).find(cookie => cookie.name === "BankFlow.Session");
	expect(cookie?.httpOnly).toBe(true);
	expect(cookie?.sameSite).toBe("Strict");
	expect(await page.evaluate(() => localStorage.length)).toBe(0);
	await page.reload();
	await expect(page.getByRole("heading", { name: "Cuentas bancarias" })).toBeVisible();
	const number = `E2E${Date.now()}`;
	const clientEmail = `client-${number.toLowerCase()}@bankflow.test`;
	const clientPassword = "TestingOnly123!";
	await page.getByRole("button", { name: "Nuevo cliente", exact: true }).click();
	await page.getByLabel("Correo del cliente").fill(clientEmail);
	await page.getByLabel("Contrasena inicial", { exact: true }).fill(clientPassword);
	await page.getByLabel("Confirmar contrasena", { exact: true }).fill("mismatch");
	await page.getByRole("button", { name: "Crear cliente", exact: true }).click();
	await expect(page.getByRole("alert")).toContainText("no coinciden");
	await page.getByLabel("Confirmar contrasena", { exact: true }).fill(clientPassword);
	await page.getByRole("button", { name: "Crear cliente", exact: true }).click();
	await expect(page.getByRole("dialog")).not.toBeVisible();
	await expect(page.locator(".client-row").filter({ hasText: clientEmail })).toBeVisible();
	await page.getByRole("button", { name: "Nueva cuenta" }).click();
	await page.getByRole("combobox", { name: "Cliente propietario" }).click();
	await page.getByRole("option", { name: clientEmail, exact: true }).click();
	await page.getByLabel("Titular", { exact: true }).fill("Operador de pruebas");
	await page.getByLabel("Numero de cuenta", { exact: true }).fill(number);
	await page.getByRole("button", { name: "Confirmar", exact: true }).click();
	await expect(page.getByRole("dialog")).not.toBeVisible();
	await page.getByLabel("Buscar titular o cuenta").fill(number);
	await expect(page.locator("tbody tr")).toHaveCount(1);
	await page.getByRole("button", { name: `Depositar en ${number}` }).click();
	await page.getByLabel("Importe (EUR)", { exact: true }).fill("250");
	await page.getByRole("button", { name: "Confirmar", exact: true }).click();
	await expect(page.getByRole("dialog")).not.toBeVisible();
	await expect(page.locator("tbody .balance")).toContainText("250,00");
	await page.getByRole("button", { name: `Retirar de ${number}` }).click();
	await page.getByLabel("Importe (EUR)", { exact: true }).fill("300");
	await page.getByRole("button", { name: "Confirmar", exact: true }).click();
	await expect(page.getByRole("alert")).toContainText("saldo insuficiente");
	await page.getByLabel("Importe (EUR)", { exact: true }).fill("50");
	await page.getByRole("button", { name: "Confirmar", exact: true }).click();
	await expect(page.getByRole("dialog")).not.toBeVisible();
	await expect(page.locator("tbody .balance")).toContainText("200,00");
	await page.getByRole("button", { name: `Historial de ${number}` }).click();
	await expect(page.locator(".transaction")).toHaveCount(2);
	expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
	await page.screenshot({ path: testInfo.outputPath("workspace.png"), fullPage: true, animations: "disabled" });
	const operatorToken = decodeURIComponent((await context.cookies()).find(cookie => cookie.name === "XSRF-TOKEN")!.value);
	const extraClient = await page.request.post("/api/clients", {
		headers: { "X-XSRF-TOKEN": operatorToken },
		data: { email: `other-${number.toLowerCase()}@bankflow.test`, password: clientPassword }
	});
	expect(extraClient.status()).toBe(201);
	const otherAccount = await page.request.post("/api/accounts", {
		headers: { "X-XSRF-TOKEN": operatorToken },
		data: { customerName: "Otro cliente", accountNumber: `X${number}`, ownerUserId: (await extraClient.json()).id }
	});
	expect(otherAccount.status()).toBe(201);
	const otherId = (await otherAccount.json()).id;
	await page.getByRole("button", { name: "Cerrar sesion", exact: true }).click();
	await expect(page).toHaveURL(/\/login$/);
	await page.getByLabel("Correo electronico").fill(clientEmail);
	await page.getByLabel("Contrasena", { exact: true }).fill(clientPassword);
	await page.getByRole("button", { name: "Iniciar sesion" }).click();
	await expect(page.getByRole("heading", { name: "Mis cuentas", exact: true })).toBeVisible();
	await expect(page.locator("tbody tr")).toHaveCount(1);
	await expect(page.locator("tbody .account-number")).toHaveText(number);
	await expect(page.getByRole("button", { name: "Nueva cuenta" })).toHaveCount(0);
	await expect(page.getByRole("button", { name: "Nuevo cliente", exact: true })).toHaveCount(0);
	await expect(page.getByRole("button", { name: `Depositar en ${number}` })).toHaveCount(0);
	await expect(page.getByRole("button", { name: `Retirar de ${number}` })).toHaveCount(0);
	await expect(page.locator(".clients-section")).toHaveCount(0);
	expect((await page.request.get(`/api/accounts/${otherId}`)).status()).toBe(404);
	expect((await page.request.get(`/api/accounts/${otherId}/transactions`)).status()).toBe(404);
	expect((await page.request.get("/api/clients")).status()).toBe(403);
	const clientToken = decodeURIComponent((await context.cookies()).find(cookie => cookie.name === "XSRF-TOKEN")!.value);
	const forbidden = await page.request.post(`/api/accounts/${otherId}/deposit`, { headers: { "X-XSRF-TOKEN": clientToken }, data: { amount: 10 } });
	expect(forbidden.status()).toBe(403);
	await page.getByRole("button", { name: `Historial de ${number}` }).click();
	await expect(page.locator(".transaction")).toHaveCount(2);
	await page.reload();
	await expect(page.getByRole("heading", { name: "Mis cuentas", exact: true })).toBeVisible();
	await expect(page.locator("tbody tr")).toHaveCount(1);
	let originalKey = "";
	await page.route("**/api/transfers", async route => {
		originalKey = route.request().headers()["idempotency-key"];
		const committed = await route.fetch();
		expect(committed.status()).toBe(201);
		await route.abort("failed");
	}, { times: 1 });
	await page.getByRole("button", { name: `Transferir desde ${number}` }).click();
	await page.getByLabel("Cuenta destino", { exact: true }).fill(`X${number}`);
	await page.getByLabel("Importe (EUR)", { exact: true }).fill("50");
	await page.getByLabel("Concepto", { exact: true }).fill("Transferencia de prueba");
	await page.getByRole("button", { name: "Revisar", exact: true }).click();
	await expect(page.getByRole("heading", { name: "Confirmar transferencia", exact: true })).toBeVisible();
	await page.getByRole("button", { name: "Confirmar transferencia", exact: true }).click();
	await expect(page.getByRole("alert")).toContainText("No se pudo confirmar el resultado");
	await page.getByRole("dialog").getByRole("button", { name: "Cerrar", exact: true }).click();
	await expect(page.locator(".pending-transfer")).toBeVisible();
	await page.reload();
	await expect(page.locator(".pending-transfer")).toBeVisible();
	await expect(page.locator("tbody .balance")).toContainText("150,00");
	await page.getByRole("button", { name: "Revisar transferencia", exact: true }).click();
	await page.route("**/api/transfers", route => route.fulfill({ status: 401, contentType: "application/json", body: "{}" }), { times: 1 });
	await page.getByRole("button", { name: "Reintentar transferencia", exact: true }).click();
	await expect(page).toHaveURL(/\/login$/);
	await expect(page.getByRole("dialog")).toHaveCount(0);
	await page.getByLabel("Correo electronico").fill(clientEmail);
	await page.getByLabel("Contrasena", { exact: true }).fill(clientPassword);
	await page.getByRole("button", { name: "Iniciar sesion" }).click();
	await expect(page.locator(".pending-transfer")).toBeVisible();
	await page.getByRole("button", { name: "Revisar transferencia", exact: true }).click();
	const repeatedResponse = page.waitForResponse(response => response.url().endsWith("/api/transfers") && response.request().method() === "POST");
	await page.getByRole("button", { name: "Reintentar transferencia", exact: true }).click();
	const repeated = await repeatedResponse;
	expect(repeated.status()).toBe(200);
	expect(repeated.request().headers()["idempotency-key"]).toBe(originalKey);
	await expect(page.getByRole("heading", { name: "Transferencia completada" })).toBeVisible();
	await page.screenshot({ path: testInfo.outputPath("transfer-receipt.png"), fullPage: true, animations: "disabled" });
	const downloadPromise = page.waitForEvent("download");
	await page.getByRole("button", { name: "Comprobante", exact: true }).click();
	expect((await downloadPromise).suggestedFilename()).toMatch(/^bankflow-.*\.json$/);
	await page.getByRole("dialog").getByRole("button", { name: "Cerrar", exact: true }).click();
	await expect(page.locator(".pending-transfer")).toHaveCount(0);
	await expect(page.locator("tbody .balance")).toContainText("150,00");
	await page.getByRole("button", { name: `Historial de ${number}` }).click();
	await expect(page.locator(".transaction")).toHaveCount(3);
	await expect(page.getByText("Transferencia enviada", { exact: true })).toBeVisible();
	expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
	await page.screenshot({ path: testInfo.outputPath("client.png"), fullPage: true, animations: "disabled" });
	await page.getByRole("button", { name: "Cerrar sesion", exact: true }).click();
	await page.goto("/");
	await expect(page).toHaveURL(/\/login$/);
	expect(errors).toEqual([]);
});
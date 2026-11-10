import { Routes } from "@angular/router";
import { backofficeGuard, bankingUserGuard, sessionGuard } from "./session";

export const routes: Routes = [
	{ path: "login", loadComponent: () => import("./login").then(module => module.Login) },
	{ path: "payments", canActivate: [bankingUserGuard], loadComponent: () => import("./payments").then(module => module.Payments) },
	{ path: "backoffice", canActivate: [backofficeGuard], loadComponent: () => import("./backoffice").then(module => module.Backoffice) },
	{ path: "", canActivate: [sessionGuard], loadComponent: () => import("./dashboard").then(module => module.Dashboard) },
	{ path: "**", redirectTo: "" }
];

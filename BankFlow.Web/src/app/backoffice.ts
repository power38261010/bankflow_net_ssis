import { Component, inject, signal } from "@angular/core";
import { CurrencyPipe, DatePipe } from "@angular/common";
import { RouterLink } from "@angular/router";
import { MatButtonModule } from "@angular/material/button";
import { MatIconModule } from "@angular/material/icon";
import { MatProgressBarModule } from "@angular/material/progress-bar";
import { MatTooltipModule } from "@angular/material/tooltip";
import { BackofficeOverview, Banking } from "./banking";
import { errorMessage, Session } from "./session";

@Component({
	selector: "app-backoffice",
	imports: [CurrencyPipe, DatePipe, RouterLink, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule],
	templateUrl: "./backoffice.html",
	styleUrl: "./backoffice.scss"
})
export class Backoffice {
	readonly session = inject(Session);
	private readonly banking = inject(Banking);
	readonly overview = signal<BackofficeOverview | null>(null);
	readonly loading = signal(false);
	readonly error = signal("");
	readonly loggingOut = signal(false);

	constructor() { void this.load(); }

	async load() {
		this.loading.set(true);
		this.error.set("");
		try {
			this.overview.set(await this.banking.backofficeOverview());
		} catch (error) {
			this.error.set(errorMessage(error));
		} finally {
			this.loading.set(false);
		}
	}

	async logout() {
		this.loggingOut.set(true);
		try {
			await this.session.logout();
			location.assign("/login");
		} catch (error) {
			this.error.set(errorMessage(error));
			this.loggingOut.set(false);
		}
	}
}

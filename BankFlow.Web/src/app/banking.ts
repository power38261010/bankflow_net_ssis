import { inject, Injectable } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { firstValueFrom } from "rxjs";

export interface Account {
	id: string;
	customerName: string;
	accountNumber: string;
	balance: number;
	createdAt: string;
	ownerUserId: string | null;
}
export interface Client { id: string; email: string; }
export interface PageResult<T> { items: T[]; page: number; pageSize: number; totalCount: number; }
export interface AccountSummary { accountCount: number; totalBalance: number; }
export interface BackofficeTransfer {
	id: string; sourceAccountLastFour: string; destinationAccountLastFour: string; amount: number; createdAt: string;
}
export interface BackofficeOverview {
	accountCount: number; totalBalance: number; clientCount: number; transfersLast30Days: number;
	transferVolumeLast30Days: number; pendingPayments: number; failedPayments: number;
	depositsLast30Days: number; withdrawalsLast30Days: number; recentTransfers: BackofficeTransfer[];
}
export interface Beneficiary { id: string; name: string; accountNumber: string; createdAt: string; }
export interface ScheduledDraft { key: string; sourceAccountId: string; beneficiaryId: string; amount: number; description: string; frequency: number; dueAt: string; }
export interface ScheduledPayment {
	id: string; sourceAccountNumber: string; beneficiaryName: string; destinationAccountNumber: string;
	amount: number; description: string; frequency: number; status: number; nextRunAt: string;
	lastRunAt: string | null; lastTransferId: string | null; failureCode: string | null;
	canCancel: boolean;
}
export interface TransferReceipt {
	id: string;
	sourceAccountId: string;
	destinationAccountNumber: string;
	amount: number;
	description: string;
	createdAt: string;
}
export interface TransferDraft {
	key: string;
	sourceAccountId: string;
	destinationAccountNumber: string;
	amount: number;
	description: string;
}
export interface Transaction {
	id: string;
	amount: number;
	type: string;
	description: string;
	createdAt: string;
	transferId: string | null;
}
@Injectable({ providedIn: "root" })
export class Banking {
	private readonly http = inject(HttpClient);
	accounts() { return firstValueFrom(this.http.get<Account[]>("/api/accounts")); }
	accountPage(page: number, pageSize: number, search: string) {
		return firstValueFrom(this.http.get<PageResult<Account>>("/api/accounts/page", { params: { page, pageSize, search } }));
	}
	summary() { return firstValueFrom(this.http.get<AccountSummary>("/api/accounts/summary")); }
	backofficeOverview() { return firstValueFrom(this.http.get<BackofficeOverview>("/api/backoffice/overview")); }
	account(id: string) { return firstValueFrom(this.http.get<Account>(`/api/accounts/${id}`)); }
	movementPage(id: string, page: number, pageSize: number) {
		return firstValueFrom(this.http.get<PageResult<Transaction>>(`/api/accounts/${id}/transactions/page`, { params: { page, pageSize } }));
	}
	history(id: string) { return firstValueFrom(this.http.get<Transaction[]>(`/api/accounts/${id}/transactions`)); }
	beneficiaries(page = 1, pageSize = 20) { return firstValueFrom(this.http.get<PageResult<Beneficiary>>("/api/beneficiaries", { params: { page, pageSize } })); }
	createBeneficiary(name: string, accountNumber: string) { return firstValueFrom(this.http.post<Beneficiary>("/api/beneficiaries", { name, accountNumber })); }
	archiveBeneficiary(id: string) { return firstValueFrom(this.http.delete<void>(`/api/beneficiaries/${id}`)); }
	payments(page = 1, pageSize = 20) { return firstValueFrom(this.http.get<PageResult<ScheduledPayment>>("/api/scheduled-payments", { params: { page, pageSize } })); }
	schedule(draft: ScheduledDraft) {
		const { key, ...payload } = draft;
		return firstValueFrom(this.http.post<{ id: string }>("/api/scheduled-payments", payload, { headers: { "Idempotency-Key": key } }));
	}
	pendingSchedule(userId: string): ScheduledDraft | null {
		try {
			const draft = JSON.parse(sessionStorage.getItem(`bankflow.schedule.${userId}`) ?? "null");
			if (draft && typeof draft.key === "string" && typeof draft.sourceAccountId === "string" && typeof draft.beneficiaryId === "string" &&
				typeof draft.amount === "number" && Number.isFinite(draft.amount) && typeof draft.description === "string" &&
				typeof draft.frequency === "number" && typeof draft.dueAt === "string") return draft;
		} catch { }
		return null;
	}
	saveSchedule(userId: string, draft: ScheduledDraft) { sessionStorage.setItem(`bankflow.schedule.${userId}`, JSON.stringify(draft)); }
	clearSchedule(userId: string) { sessionStorage.removeItem(`bankflow.schedule.${userId}`); }
	cancelPayment(id: string) { return firstValueFrom(this.http.post<void>(`/api/scheduled-payments/${id}/cancel`, {})); }
	create(customerName: string, accountNumber: string, ownerUserId: string) {
		return firstValueFrom(this.http.post<Account>("/api/accounts", { customerName, accountNumber, ownerUserId }));
	}
	clients() { return firstValueFrom(this.http.get<Client[]>("/api/clients")); }
	createClient(email: string, password: string) {
		return firstValueFrom(this.http.post<Client>("/api/clients", { email, password }));
	}
	assignOwner(id: string, ownerUserId: string) {
		return firstValueFrom(this.http.put<Account>(`/api/accounts/${id}/owner`, { ownerUserId }));
	}
	operation(id: string, action: "deposit" | "withdraw", amount: number) {
		return firstValueFrom(this.http.post<Account>(`/api/accounts/${id}/${action}`, { amount }));
	}
	transfer(draft: TransferDraft) {
		const { key, ...payload } = draft;
		return firstValueFrom(this.http.post<TransferReceipt>("/api/transfers", payload, { headers: { "Idempotency-Key": key } }));
	}
	pendingTransfer(userId: string): TransferDraft | null {
		try {
			const value = JSON.parse(sessionStorage.getItem(`bankflow.transfer.${userId}`) ?? "null");
			if (value && typeof value.key === "string" && typeof value.sourceAccountId === "string" &&
				typeof value.destinationAccountNumber === "string" && typeof value.amount === "number" &&
				Number.isFinite(value.amount) && typeof value.description === "string") return value;
		} catch { return null; }
		return null;
	}
	savePending(userId: string, draft: TransferDraft) { sessionStorage.setItem(`bankflow.transfer.${userId}`, JSON.stringify(draft)); }
	clearPending(userId: string) { sessionStorage.removeItem(`bankflow.transfer.${userId}`); }
}
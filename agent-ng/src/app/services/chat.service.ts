import { Injectable } from '@angular/core';
import { BffApiService } from './bff/bff-api.service';

export interface ChatAttachment {
  name: string;
  mime: string;
  dataBase64: string;
}

// Talks to the cloud Agent Builder agent through the BFF (/api/chat -> agent -> dev tunnel -> MCP).
// Keeps one conversationId per app session so the agent retains context across turns.
@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly conversationId =
    (globalThis as any).crypto?.randomUUID?.() ?? `conv-${Date.now()}-${Math.random().toString(36).slice(2)}`;

  constructor(private readonly bff: BffApiService) {}

  async send(
    message: string,
    attachments: ChatAttachment[],
    context: { businessObjectType?: string; businessObjectId?: string } | null,
  ): Promise<string> {
    const data = await this.bff.post<any>('/api/chat', {
      message,
      conversationId: this.conversationId,
      attachments,
      businessObjectType: context?.businessObjectType ?? null,
      businessObjectId: context?.businessObjectId ?? null,
    });
    return data?.reply ?? '(no reply)';
  }
}

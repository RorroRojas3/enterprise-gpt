import {
  Component,
  output,
  OnDestroy,
  inject,
  DestroyRef,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { StoreService } from '../../../store/store.service';
import { ModelStore } from '../../../store/model.store';
import { McpStore } from '../../../store/mcp.store';
import { ConversationService } from '../../../services/conversation.service';
import { FormsModule } from '@angular/forms';
import {
  firstValueFrom,
  Subscription,
  interval,
  switchMap,
  takeWhile,
} from 'rxjs';
import markdownit from 'markdown-it';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { createMessage } from '../../../dtos/MessageDto';
import hljs from 'highlight.js';
import markdown_it_highlightjs from 'markdown-it-highlightjs';

import { DocumentService } from '../../../services/document.service';
import { ModelService } from '../../../services/model.service';
import { ModelDto } from '../../../dtos/ModelDto';
import { AiServiceType } from '../../../dtos/const/AiServiceType';
import { JobDto } from '../../../dtos/JobDto';
import { JobStatusDto } from '../../../dtos/JobStatusDto';
import { FileUploadDto } from '../../../dtos/FileUploadDto';
import { JobStatus } from '../../../dtos/const/JobStatus';
import { JobState } from '../../../dtos/const/JobState';
import { Location } from '@angular/common';
import { DocumentFormats } from '../../../dtos/const/DocumentFormats';
import { McpDto } from '../../../dtos/McpDto';

@Component({
  selector: 'app-prompt-box',
  imports: [FormsModule],
  templateUrl: './prompt-box.component.html',
  styleUrl: './prompt-box.component.scss',
})
export class PromptBoxComponent implements OnDestroy {
  // Constants
  readonly JOB_POLLING_INTERVAL_MS = 5000;

  public readonly storeService = inject(StoreService);
  public readonly modelStore = inject(ModelStore);
  public readonly mcpStore = inject(McpStore);
  private readonly conversationService = inject(ConversationService);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly documentService = inject(DocumentService);
  private readonly modelService = inject(ModelService);
  private readonly location = inject(Location);
  private readonly destroyRef = inject(DestroyRef);

  // Use Angular 19 output() function
  markdown = output<SafeHtml>();

  prompt = '';
  message = '';
  private readonly md: markdownit;
  private sseSubscription: Subscription | undefined;
  sanitizeHtml!: SafeHtml;

  // File handling properties
  attachedFiles: FileUploadDto[] = [];
  showAttachedFiles = true;
  isDragOver = false;
  private jobPollingSubscriptions: Map<string, Subscription> = new Map();

  constructor() {
    this.md = new markdownit({
      html: true,
      linkify: true,
      typographer: true,
    }).use(markdown_it_highlightjs, { hljs });
  }

  async onClickCreateConversation(): Promise<void> {
    if (!this.prompt.trim() || this.storeService.isStreaming()) {
      return;
    }

    try {
      const promptText = this.prompt;
      this.prompt = '';
      this.showAttachedFiles = false;
      this.storeService.messages.update((messages) => [
        ...messages,
        createMessage(promptText, true, undefined),
      ]);

      this.storeService.stream.set('');
      this.storeService.isStreaming.set(true);
      this.storeService.showStreamLoader.set(true);

      if (!this.storeService.conversation()) {
        const conversation = await firstValueFrom(
          this.conversationService.createConversation({
            projectId: this.storeService.projectId(),
          }),
        );
        this.storeService.conversation.set(conversation);
        this.location.replaceState(`chat/conversation/${conversation.id}`);
      }

      // Upload attached files to the conversation
      if (this.attachedFiles.length > 0) {
        const jobs = await this.uploadAttachedFiles();
        // Start polling for job status
        await this.pollJobStatuses(jobs);
        this.clearAllFiles();
      }

      let firstStream = true;
      this.sseSubscription = this.conversationService
        .getServerSentEvent(promptText)
        .subscribe({
          next: (message) => {
            if (firstStream) {
              firstStream = false;
              this.storeService.showStreamLoader.set(false);
            }
            this.storeService.stream.update((stream) => stream + message);
            const html = this.md.render(this.storeService.stream());
            this.sanitizeHtml = this.sanitizer.bypassSecurityTrustHtml(html);
            this.markdown.emit(this.sanitizeHtml);
          },
          complete: () => {
            const rawContent = this.storeService.stream();
            this.storeService.stream.set('');
            this.storeService.messages.update((messages) => [
              ...messages,
              createMessage(rawContent, false, this.sanitizeHtml),
            ]);
            this.storeService.isStreaming.set(false);
            this.storeService.streamMessage.set(
              createMessage('', false, undefined),
            );
            this.showAttachedFiles = true;
            this.conversationService.loadMenuConversations().subscribe();
          },
          error: (error) => {
            this.resetPromptState();
          },
        });
    } catch (error) {
      // Handle errors appropriately
      console.error('Error in conversation creation:', error);
      this.resetPromptState();
    }
  }

  /**
   * Stops the current streaming conversation.
   *
   * Unsubscribes from the SSE (Server-Sent Events) subscription if active,
   * clears the subscription reference, resets streaming state, clears the
   * stream content, and resets the prompt input.
   *
   * @returns {void}
   */
  onClickStopConversation(): void {
    if (this.sseSubscription) {
      this.sseSubscription.unsubscribe();
      this.sseSubscription = undefined;
    }
    this.resetPromptState();
  }

  /**
   * Resets the prompt input state and clears all associated data.
   * This includes resetting streaming state, clearing the prompt text,
   * clearing attached files, and showing the file attachment area.
   *
   * @returns {void}
   */
  resetPromptState(): void {
    this.storeService.isStreaming.set(false);
    this.storeService.showStreamLoader.set(false);
    this.storeService.stream.set('');
    this.storeService.streamMessage.set(createMessage('', false, undefined));
    this.prompt = '';
    this.showAttachedFiles = true;
    this.clearAllFiles();
  }

  /**
   * Handles model selection: updates the store immediately for a snappy UI,
   * then persists the choice as the user's favorite (last picked).
   *
   * @param model - The newly selected model
   */
  onModelChange(model: ModelDto): void {
    this.modelStore.setSelectedModel(model);
    this.modelService
      .setFavoriteModel(model.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((models) => this.modelStore.setModels(models));
  }

  /**
   * Toggles MCP selection
   */
  toggleMcpSelection(mcp: McpDto, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.mcpStore.toggleMcpSelection(mcp);
  }

  /**
   * Checks if an MCP is selected
   */
  isMcpSelected(mcp: McpDto): boolean {
    return this.mcpStore.isMcpSelected(mcp);
  }

  /**
   * Gets display text for MCP dropdown button
   */
  getMcpButtonText(): string {
    const selectedCount = this.mcpStore.selectedMcps().length;
    if (selectedCount === 0) {
      return 'Tools';
    } else if (selectedCount === 1) {
      return this.mcpStore.selectedMcps()[0].name;
    } else {
      return `${selectedCount} Tools`;
    }
  }

  // File selection handler
  onFileSelect(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files) {
      this.handleFiles(input.files);
    }
    // Reset the input value to allow selecting the same file again
    input.value = '';
  }

  // Handle multiple files
  handleFiles(fileList: FileList): void {
    const allowedExtensions = this.storeService.fileExtensions();

    // Don't accept any files if no extensions are configured
    if (allowedExtensions.length === 0) {
      console.warn('No file types are currently allowed for upload.');
      return;
    }

    Array.from(fileList).forEach((file) => {
      const fileName = file.name.toLowerCase();

      // Check if file extension matches any allowed extension
      const isAllowed = allowedExtensions.some((ext) =>
        fileName.endsWith(ext.toLowerCase()),
      );

      if (isAllowed) {
        const attachedFile: FileUploadDto = {
          id: crypto.randomUUID(),
          name: file.name,
          size: file.size,
          file: file,
          status: undefined,
          progress: 0,
        };
        this.attachedFiles.push(attachedFile);
      } else {
        // Show error message for unsupported files
        const allowedList = allowedExtensions.join(', ');
        console.warn(
          `File "${file.name}" is not supported. Allowed types: ${allowedList}`,
        );
        // You could also show a toast notification or alert here
      }
    });
  }

  // Remove a specific file
  removeFile(fileId: string): void {
    this.attachedFiles = this.attachedFiles.filter(
      (file) => file.id !== fileId,
    );
  }

  // Clear all attached files
  clearAllFiles(): void {
    this.attachedFiles = [];
  }

  // Format file size for display
  formatFileSize(bytes: number): string {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  }

  // Upload all attached files to the current conversation
  async uploadAttachedFiles(): Promise<JobDto[]> {
    const conversationId = this.storeService.conversation()!.id;
    if (!conversationId) {
      throw new Error('No conversation ID available for file upload');
    }

    // Update file status to uploading
    this.attachedFiles.forEach((file) => {
      file.status = JobStatus.Queued;
    });

    // Upload files sequentially and wait for each to complete
    const uploadPromises = this.attachedFiles.map((attachedFile) =>
      firstValueFrom(
        this.documentService.createConversationDocument(
          conversationId,
          attachedFile.file,
        ),
      ),
    );

    const jobs = await Promise.all(uploadPromises);
    return jobs;
  }

  // Poll job statuses for uploaded files
  async pollJobStatuses(jobs: JobDto[]): Promise<void> {
    const pollingPromises = jobs.map((job, index) => {
      const attachedFile = this.attachedFiles[index];
      if (attachedFile) {
        return this.startJobPolling(job, attachedFile);
      }
      return Promise.resolve();
    });

    await Promise.all(pollingPromises);
  }

  // Start polling for a specific job
  startJobPolling(job: JobDto, attachedFile: FileUploadDto): Promise<void> {
    return new Promise((resolve, reject) => {
      const subscription = interval(this.JOB_POLLING_INTERVAL_MS)
        .pipe(
          switchMap(() => this.documentService.getUploadStatus(job)),
          takeUntilDestroyed(this.destroyRef),
          takeWhile(
            (status: JobStatusDto) =>
              status.state !== JobState.Succeeded &&
              status.state !== JobState.Failed,
            true,
          ),
        )
        .subscribe({
          next: (result: JobStatusDto) => {
            // Update attached file status
            attachedFile.progress = result.progress;

            if (result.state === JobState.Succeeded) {
              attachedFile.status = result.status;
              console.log(`File ${attachedFile.name} upload succeeded`);
              this.jobPollingSubscriptions.delete(attachedFile.id);
              resolve();
            } else if (result.state === JobState.Failed) {
              attachedFile.status = result.status;
              console.error(
                `File ${attachedFile.name} upload failed: ${result.status}`,
              );
              this.jobPollingSubscriptions.delete(attachedFile.id);
              reject(new Error(`Upload failed: ${result.status}`));
            } else {
              attachedFile.status = result.status;
              attachedFile.progress = result.progress;
              console.log(
                `File ${attachedFile.name} processing: ${result.progress}%`,
              );
            }
          },
          error: (error) => {
            attachedFile.status = JobStatus.Failed;
            console.error(
              `Error polling status for file ${attachedFile.name}:`,
              error,
            );
            this.jobPollingSubscriptions.delete(attachedFile.id);
            reject(error);
          },
          complete: () => {
            this.jobPollingSubscriptions.delete(attachedFile.id);
          },
        });

      // Store the subscription for cleanup
      this.jobPollingSubscriptions.set(attachedFile.id, subscription);
    });
  }

  // Drag and drop event handlers
  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver = true;
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver = false;
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver = false;

    const files = event.dataTransfer?.files;
    if (files) {
      this.handleFiles(files);
    }
  }

  /**
   * Handles keydown events on the textarea
   * Sends message on Enter (but allows Shift+Enter for new lines)
   */
  onTextareaKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.onClickCreateConversation();
    }
  }

  /**
   * Handles keydown events on the form
   * Prevents form submission on Enter key in form elements
   */
  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
    }
  }

  /**
   * Gets the appropriate Bootstrap icon class based on the AI service ID.
   * @param aiServiceId The unique identifier for the AI service
   * @returns Bootstrap icon class string
   */
  getServiceIcon(aiServiceId: string): string {
    switch (aiServiceId) {
      case AiServiceType.AzureAIFoundry:
        return 'bi bi-openai';
      default:
        return 'bi bi-question-circle'; // Question circle for unknown services
    }
  }

  /**
   * Downloads conversation history in the specified format
   */
  onDownloadConversationHistory(format: DocumentFormats): void {
    const conversationId = this.storeService.conversation()?.id;
    if (!conversationId) {
      return;
    }

    this.documentService
      .getConversationHistory(conversationId, format)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((response) => {
        this.documentService.downloadFile(response.blob, response.fileName);
      });
  }

  /**
   * Gets the Bootstrap icon class for document format
   */
  getFormatIcon(format: DocumentFormats): string {
    switch (format) {
      case DocumentFormats.PDF:
        return 'bi bi-file-earmark-pdf';
      case DocumentFormats.DOCX:
        return 'bi bi-file-earmark-word';
      case DocumentFormats.MARKDOWN:
        return 'bi bi-markdown';
      default:
        return 'bi bi-file-earmark';
    }
  }

  /**
   * Gets the display name for document format
   */
  getFormatName(format: DocumentFormats): string {
    switch (format) {
      case DocumentFormats.PDF:
        return 'PDF';
      case DocumentFormats.DOCX:
        return 'Word';
      case DocumentFormats.MARKDOWN:
        return 'Markdown';
      default:
        return 'Unknown';
    }
  }

  /**
   * Gets all available document formats
   */
  getDocumentFormats(): DocumentFormats[] {
    return [
      DocumentFormats.PDF,
      DocumentFormats.DOCX,
      DocumentFormats.MARKDOWN,
    ];
  }

  /**
   * Gets the accepted file types for the file input based on store configuration
   * @returns Comma-separated string of accepted file extensions, or empty string if none configured
   */
  getAcceptedFileTypes(): string {
    const extensions = this.storeService.fileExtensions();
    if (extensions.length === 0) {
      return ''; // Don't accept any files if no extensions configured
    }

    // Extensions from API already include the dot (e.g., ".pdf", ".csv")
    return extensions.join(',');
  }

  /**
   * Checks if file attachments are allowed based on configured extensions
   * @returns true if file attachments are allowed
   */
  isFileAttachmentAllowed(): boolean {
    return this.storeService.fileExtensions().length > 0;
  }

  /**
   * Angular lifecycle hook that is called when the component is destroyed.
   * Cleans up the SSE (Server-Sent Events) subscription to prevent memory leaks.
   */
  ngOnDestroy(): void {
    if (this.sseSubscription) {
      this.sseSubscription.unsubscribe();
    }

    // Clean up all job polling subscriptions
    this.jobPollingSubscriptions.forEach((subscription) =>
      subscription.unsubscribe(),
    );
    this.jobPollingSubscriptions.clear();
  }
}

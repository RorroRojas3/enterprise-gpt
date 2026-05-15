export interface ErrorDto {
  statusCode: number;
  errors: string[];
  traceId: string;
  timestamp: string;
}

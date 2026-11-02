import { daemonRequest } from './daemon-rpc'
import { DaemonRequestError } from '../../shared/ipc'

/**
 * 语音能力客户端：录音 → ASR 转写、TTS 合成 → 朗读播放。
 * 与豆包语音同源（火山 openspeech），经 daemon RPC 直连，不占用模型回合。
 */

export interface SynthesizedSpeech {
  file: string
  format: string
}

/** 判断语音能力是否可用：未配置 speech.apiKey 时 daemon 返回 invalidParams。 */
export function isSpeechUnavailableError(error: unknown): boolean {
  return error instanceof DaemonRequestError && error.code === 'invalidParams'
}

/** ASR 转写：audioData 为 16kHz 单声道 16-bit WAV 的 base64。 */
export async function transcribeAudio(audioDataB64: string): Promise<string> {
  const message = await daemonRequest('speech.transcribe', { audioData: audioDataB64 })
  const payload = JSON.parse(message.data) as { text?: string }
  const text = payload.text?.trim()
  if (!text) throw new Error('未识别到语音内容（音频可能为空或格式不受支持）')
  return text
}

/** TTS 合成：返回落盘在 .haoyue/outputs/audio 的音频文件路径。 */
export async function synthesizeSpeech(text: string): Promise<SynthesizedSpeech> {
  const message = await daemonRequest('speech.synthesize', { text })
  const payload = JSON.parse(message.data) as { file?: string; format?: string }
  if (!payload.file) throw new Error('语音合成未返回音频产物')
  return { file: payload.file, format: payload.format ?? 'mp3' }
}

/** 读取本地音频产物并返回 <audio> 播放器实例（调用方负责 audio.play()）。 */
export async function loadPlayableAudio(filePath: string): Promise<HTMLAudioElement> {
  const media = await window.haoyue.readMediaBase64(filePath)
  if (!media || media.data.length === 0) throw new Error('无法读取音频文件')
  return new Audio(`data:${media.mediaType};base64,${media.data}`)
}

/** 录音会话：startRecording() 后调用 stop() 结束并返回 WAV(base64)。 */
export interface RecordingSession {
  stop(): Promise<string>
  /** 已录制秒数（整秒）。 */
  seconds(): number
}

function pickRecordingMime(): string {
  if (typeof MediaRecorder === 'undefined') throw new Error('当前环境不支持录音')
  const candidates = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus']
  for (const mime of candidates) {
    if (MediaRecorder.isTypeSupported(mime)) return mime
  }
  return ''
}

/** MediaRecorder(webm/opus) → AudioContext 解码 → 16kHz 单声道 16-bit PCM WAV(base64)。 */
async function toWavBase64(webm: Blob): Promise<string> {
  const bytes = await webm.arrayBuffer()
  const audioContext = new AudioContext()
  try {
    const decoded = await audioContext.decodeAudioData(bytes)
    const targetRate = 16000
    const length = Math.max(1, Math.ceil(decoded.duration * targetRate))
    const offline = new OfflineAudioContext(1, length, targetRate)
    const source = offline.createBufferSource()
    source.buffer = decoded
    source.connect(offline.destination)
    source.start(0)
    const rendered = await offline.startRendering()
    const samples = rendered.getChannelData(0)

    const dataLength = samples.length * 2
    const buffer = new ArrayBuffer(44 + dataLength)
    const view = new DataView(buffer)
    const writeAscii = (offset: number, text: string): void => {
      for (let i = 0; i < text.length; i += 1) view.setUint8(offset + i, text.charCodeAt(i))
    }
    writeAscii(0, 'RIFF')
    view.setUint32(4, 36 + dataLength, true)
    writeAscii(8, 'WAVE')
    writeAscii(12, 'fmt ')
    view.setUint32(16, 16, true)
    view.setUint16(20, 1, true) // PCM
    view.setUint16(22, 1, true) // mono
    view.setUint32(24, targetRate, true)
    view.setUint32(28, targetRate * 2, true) // byte rate
    view.setUint16(32, 2, true) // block align
    view.setUint16(34, 16, true) // bits per sample
    writeAscii(36, 'data')
    view.setUint32(40, dataLength, true)
    for (let i = 0; i < samples.length; i += 1) {
      const clamped = Math.max(-1, Math.min(1, samples[i] ?? 0))
      view.setInt16(44 + i * 2, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true)
    }

    const bytesView = new Uint8Array(buffer)
    const chunkSize = 0x8000
    const parts: string[] = []
    for (let i = 0; i < bytesView.length; i += chunkSize) {
      parts.push(String.fromCharCode(...bytesView.subarray(i, i + chunkSize)))
    }
    return btoa(parts.join(''))
  } finally {
    void audioContext.close()
  }
}

export function startRecording(): RecordingSession {
  const mime = pickRecordingMime()
  const startedAt = Date.now()
  const chunks: Blob[] = []
  let recorder: MediaRecorder | null = null
  let stream: MediaStream | null = null
  let stopRequested = false
  let stopping: Promise<string> | null = null

  const startPromise = (async () => {
    const acquired = await navigator.mediaDevices.getUserMedia({ audio: true })
    if (stopRequested) {
      acquired.getTracks().forEach((track) => track.stop())
      return
    }
    stream = acquired
    recorder = new MediaRecorder(acquired, mime ? { mimeType: mime } : undefined)
    recorder.ondataavailable = (event) => {
      if (event.data.size > 0) chunks.push(event.data)
    }
    recorder.start()
  })()

  const stop = (): Promise<string> => {
    if (stopping) return stopping
    stopRequested = true
    stopping = (async () => {
      try {
        await startPromise
        const rec = recorder
        if (rec && rec.state !== 'inactive') {
          await new Promise<void>((resolve) => {
            rec.onstop = () => resolve()
            rec.stop()
          })
        }
      } catch (error) {
        stream?.getTracks().forEach((track) => track.stop())
        throw error
      }
      stream?.getTracks().forEach((track) => track.stop())
      const blob = new Blob(chunks, { type: mime || 'audio/webm' })
      if (blob.size === 0) throw new Error('未录制到音频')
      return toWavBase64(blob)
    })()
    return stopping
  }

  return {
    stop,
    seconds: () => Math.floor((Date.now() - startedAt) / 1000)
  }
}

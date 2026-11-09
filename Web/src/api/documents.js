// Thin client for the Document API (Document/Controllers/DocumentController.cs).
// Base URL defaults to the API's dev launch profile; override with VITE_API_URL.
const baseUrl = (import.meta.env.VITE_API_URL ?? 'http://localhost:5104').replace(/\/+$/, '')

function projectUrl(project, rest) {
  const base = `${baseUrl}/${encodeURIComponent(project)}`
  const tail = (rest ?? '').toString().replace(/^\/+|\/+$/g, '')
  return tail ? `${base}/${tail}` : base
}

// 400s from the API come back as { file: [message] } or a plain string.
async function request(url, options) {
  const response = await fetch(url, options)
  if (response.ok) {
    return response.json()
  }

  let message = `${response.status} ${response.statusText}`
  const body = await response.text()
  if (body) {
    try {
      const json = JSON.parse(body)
      message = json.file?.join(' ') ?? json.title ?? (typeof json === 'string' ? json : message)
    } catch {
      message = body
    }
  }
  if (response.status === 404) {
    message = `Not found (${message})`
  }
  throw new Error(message)
}

function fileForm(file) {
  const form = new FormData()
  form.append('file', file)
  return form
}

export const listDocuments = (project, folder) => request(projectUrl(project, folder))

export const uploadDocument = (project, folder, file) =>
  request(projectUrl(project, folder), { method: 'POST', body: fileForm(file) })

export const replaceDocument = (project, id, file) =>
  request(projectUrl(project, id), { method: 'PUT', body: fileForm(file) })

export const deleteDocument = (project, id) => request(projectUrl(project, id), { method: 'DELETE' })

export const viewUrl = (project, id) => projectUrl(project, id)

export const downloadUrl = (project, id) => projectUrl(project, `${id}/download`)

export const textMessage = (id, role, text) => ({info: {id, role}, parts: [{type: 'text', text}]})
export const messageId = message => message.id ?? message.info?.id

// Package ctxio provides a reader that stops a long copy when its context is
// cancelled. Upload and download bodies are streamed with io.Copy, which never
// looks at the context; without this wrapper a client that disconnects keeps
// the worker slot busy until the whole body arrives.
package ctxio

import (
	"context"
	"io"
)

// Reader reports the context error before every Read, so copying aborts at the
// next chunk instead of running to completion.
type Reader struct {
	Ctx context.Context
	R   io.Reader
}

func (r Reader) Read(p []byte) (int, error) {
	if err := r.Ctx.Err(); err != nil {
		return 0, err
	}
	return r.R.Read(p)
}

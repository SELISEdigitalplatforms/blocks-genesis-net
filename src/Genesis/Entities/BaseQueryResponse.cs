
namespace Blocks.Genesis.Entities
{
    public class BaseQueryResponse<T>
    {
        IQueryable<T> Results { get; set; }
        public T Result { get; set; }
        public bool IsSuccess { get; set; }
        public int StatusCode { get; private set; }
        public string ErrorMessage { get; set; }
        public string PropertyName { get; set; }
        public IDictionary<string, string>? Errors { get; set; }

        public void SetSuccess(T data)
        {
            Result = data;
            IsSuccess = true;
            StatusCode = 200;
        }

        public void SetSuccess(IQueryable<T> data)
        {
            Results = data;
            IsSuccess = true;
            StatusCode = 200;
        }

        public void SetResponseError(string propertyName, string errorMessage, int statusCode, IDictionary<string, string> errors)
        {
            PropertyName = propertyName;
            ErrorMessage = errorMessage;
            Errors = errors;
            IsSuccess = false;
            StatusCode = statusCode;
        }

        public void SetResponseError(string errorMessage, int statusCode)
        {
            ErrorMessage = errorMessage;
            IsSuccess = false;
            StatusCode = statusCode;
        }
    }
}
